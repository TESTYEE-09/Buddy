using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace LethalAICrewmate
{
    /// <summary>
    /// Host-side terminal / routing helpers — mainly while in orbit (inShipPhase).
    /// </summary>
    public static class TerminalBuddy
    {
        public static bool IsInSpace()
        {
            try
            {
                var sor = StartOfRound.Instance;
                if (sor == null) return false;
                return sor.inShipPhase || !sor.shipHasLanded;
            }
            catch
            {
                return false;
            }
        }

        public static string HandleChatCommand(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return null;
            if (!CrewmateSpawner.IsHost()) return null;

            var lower = message.ToLowerInvariant();

            // strip buddy/name prefix
            string name = (Plugin.CrewmateName?.Value ?? "buddy").ToLowerInvariant();
            if (lower.StartsWith(name))
                lower = lower.Substring(name.Length).TrimStart(' ', ',', ':', '-');
            if (lower.StartsWith("buddy"))
                lower = lower.Substring(5).TrimStart(' ', ',', ':', '-');

            try
            {
                if (lower.StartsWith("route ") || lower.StartsWith("moon ") || lower.StartsWith("go to "))
                {
                    string moon = lower
                        .Replace("route ", "")
                        .Replace("moon ", "")
                        .Replace("go to ", "")
                        .Trim();
                    return RouteMoon(moon);
                }

                if (lower.StartsWith("buy "))
                    return BuyItem(lower.Substring(4).Trim());

                if (lower == "moons" || lower == "list moons" || lower == "terminal moons")
                    return ListMoons();

                if (lower == "store" || lower == "terminal store" || lower == "credits")
                    return ShowCreditsAndStoreHint();

                if (lower.StartsWith("terminal "))
                    return RunTerminalSentence(lower.Substring(9).Trim());
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"TerminalBuddy: {ex.Message}");
                return "Terminal glitched.";
            }

            return null;
        }

        public static string ApplyLlmTags(string display, ref string cleanedDisplay)
        {
            cleanedDisplay = display;
            if (string.IsNullOrEmpty(display)) return null;

            string feedback = null;
            // [ROUTE:titan] [BUY:shovel] [TERMINAL:moons]
            feedback = TryTag(ref cleanedDisplay, "ROUTE", RouteMoon) ?? feedback;
            feedback = TryTag(ref cleanedDisplay, "BUY", BuyItem) ?? feedback;
            feedback = TryTag(ref cleanedDisplay, "TERMINAL", RunTerminalSentence) ?? feedback;
            return feedback;
        }

        private static string TryTag(ref string display, string tag, Func<string, string> action)
        {
            string open = "[" + tag + ":";
            int i = display.IndexOf(open, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return null;
            int end = display.IndexOf(']', i);
            if (end < 0) return null;
            string arg = display.Substring(i + open.Length, end - (i + open.Length)).Trim();
            display = (display.Substring(0, i) + display.Substring(end + 1)).Trim();
            return action(arg);
        }

        public static string RouteMoon(string moonQuery)
        {
            if (string.IsNullOrWhiteSpace(moonQuery)) return "Which moon?";
            if (!IsInSpace())
                return "Can only route moons in orbit / before landing.";

            var sor = StartOfRound.Instance;
            var term = UnityEngine.Object.FindObjectOfType<Terminal>();
            if (sor?.levels == null) return "No moon list.";

            moonQuery = moonQuery.ToLowerInvariant().Replace("-", " ").Trim();
            int bestIdx = -1;
            string bestName = null;
            for (int i = 0; i < sor.levels.Length; i++)
            {
                var lvl = sor.levels[i];
                if (lvl == null) continue;
                string n = (lvl.PlanetName ?? lvl.name ?? "").ToLowerInvariant();
                if (n.Contains(moonQuery) || moonQuery.Contains(n) ||
                    n.Replace(" ", "").Contains(moonQuery.Replace(" ", "")))
                {
                    bestIdx = i;
                    bestName = lvl.PlanetName ?? lvl.name;
                    break;
                }
                // also match bare names like "titan", "assurance"
                if (n.Contains(moonQuery))
                {
                    bestIdx = i;
                    bestName = lvl.PlanetName ?? lvl.name;
                    break;
                }
            }

            if (bestIdx < 0)
                return $"Don't know moon '{moonQuery}'.";

            try
            {
                int credits = term != null ? term.groupCredits : 0;
                sor.ChangeLevelServerRpc(bestIdx, credits);
                Plugin.Log?.LogInfo($"Routed to level index {bestIdx} ({bestName})");
                return $"Routing to {bestName}.";
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"ChangeLevel failed: {ex.Message}");
                // fallback: try terminal sentence
                return RunTerminalSentence("route " + moonQuery);
            }
        }

        public static string BuyItem(string itemQuery)
        {
            if (string.IsNullOrWhiteSpace(itemQuery)) return "Buy what?";
            if (!IsInSpace() && StartOfRound.Instance != null && StartOfRound.Instance.shipHasLanded)
            {
                // allow buy only in orbit for safety
                return "Buy from store while in orbit.";
            }

            var term = UnityEngine.Object.FindObjectOfType<Terminal>();
            if (term == null) return "No terminal.";

            try
            {
                itemQuery = itemQuery.ToLowerInvariant();
                // buyableItemsList is Item[] on Terminal
                var list = term.buyableItemsList;
                if (list == null || list.Length == 0)
                    return RunTerminalSentence("buy " + itemQuery);

                int match = -1;
                string matchName = null;
                for (int i = 0; i < list.Length; i++)
                {
                    var item = list[i];
                    if (item == null) continue;
                    string n = (item.itemName ?? "").ToLowerInvariant();
                    if (n.Contains(itemQuery) || itemQuery.Contains(n))
                    {
                        match = i;
                        matchName = item.itemName;
                        break;
                    }
                }

                if (match < 0)
                    return $"Store doesn't have '{itemQuery}' (or name mismatch).";

                int cost = list[match].creditsWorth;
                if (term.groupCredits < cost)
                    return $"Need {cost} credits for {matchName}, have {term.groupCredits}.";

                // Buy one of that item index
                int[] bought = { match };
                int newCredits = term.groupCredits - cost;
                term.BuyItemsServerRpc(bought, newCredits, 1);
                Plugin.Log?.LogInfo($"Bought store index {match} ({matchName}) for {cost}");
                return $"Bought {matchName} ({cost} cr).";
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"BuyItem: {ex.Message}");
                return RunTerminalSentence("buy " + itemQuery);
            }
        }

        public static string ListMoons()
        {
            var sor = StartOfRound.Instance;
            if (sor?.levels == null) return "No moons loaded.";
            var names = new List<string>();
            foreach (var lvl in sor.levels)
            {
                if (lvl == null) continue;
                names.Add(lvl.PlanetName ?? lvl.name);
                if (names.Count >= 12) break;
            }
            return "Moons: " + string.Join(", ", names);
        }

        public static string ShowCreditsAndStoreHint()
        {
            var term = UnityEngine.Object.FindObjectOfType<Terminal>();
            int cr = term != null ? term.groupCredits : 0;
            return $"Credits: {cr}. Say 'buddy buy shovel' or 'buddy route titan' in orbit.";
        }

        /// <summary>Best-effort: feed a sentence into Terminal.ParsePlayerSentence / OnSubmit.</summary>
        public static string RunTerminalSentence(string sentence)
        {
            if (string.IsNullOrWhiteSpace(sentence)) return null;
            var term = UnityEngine.Object.FindObjectOfType<Terminal>();
            if (term == null) return "No terminal.";

            try
            {
                // Force text and submit like a player typed it
                if (term.screenText != null)
                {
                    // Keep a short prompt then command
                    string baseText = term.screenText.text ?? "";
                    // Append command on new line style used by terminal
                    term.screenText.text = baseText + sentence;
                    term.currentText = term.screenText.text;
                    term.textAdded = sentence.Length;
                }

                // ParsePlayerSentence reads typed words — OnSubmit is the full path
                term.OnSubmit();
                Plugin.Log?.LogInfo($"Terminal OnSubmit: '{sentence}'");
                return $"Terminal: {sentence}";
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"RunTerminalSentence: {ex.Message}");
                return "Terminal command failed.";
            }
        }
    }
}
