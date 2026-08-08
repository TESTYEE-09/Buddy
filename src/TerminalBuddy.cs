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
                if (lower.StartsWith("route ") || lower.StartsWith("moon ") ||
                    (lower.StartsWith("go to ") && !lower.Contains("ship") && !lower.Contains("home") &&
                     !lower.Contains("forward") && !lower.Contains("ahead")))
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

                if (ShipCommandParsing.IsStatusRequest(lower))
                    return BuildShipStatus(lower);

                if (ShipCommandParsing.TryParseFacilityAction(lower, out string facilityCode, out bool enableFacility))
                    return SetFacilityObject(facilityCode, enableFacility);

                if ((lower.Contains("turret") || lower.Contains("landmine") || lower.Contains("mine")) &&
                    (lower.Contains("disable") || lower.Contains("deactivate") || lower.Contains("turn off") ||
                     lower.Contains("enable") || lower.Contains("activate") || lower.Contains("turn on")))
                    return "Which terminal code? For example: buddy disable turret B3.";

                if (lower.Contains("ship door") || lower.Contains("hangar door") || lower == "open doors" || lower == "close doors")
                {
                    if (lower.Contains("open")) return SetHangarDoor(true);
                    if (lower.Contains("close") || lower.Contains("shut")) return SetHangarDoor(false);
                }

                if (lower.Contains("lights") &&
                    (lower.Contains("turn on") || lower.Contains("turn off") || lower.Contains("lights on") || lower.Contains("lights off")))
                    return SetShipLights(!lower.Contains("off"));

                if (lower == "moons" || lower == "list moons" || lower == "terminal moons")
                    return ListMoons();

                if (lower == "store" || lower == "terminal store")
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
            cleanedDisplay = StripLlmTag(display ?? "", "ROUTE");
            cleanedDisplay = StripLlmTag(cleanedDisplay, "BUY");
            cleanedDisplay = StripLlmTag(cleanedDisplay, "TERMINAL").Trim();
            return null;
        }

        private static string StripLlmTag(string value, string tag)
        {
            string open = "[" + tag + ":";
            while (true)
            {
                int start = value.IndexOf(open, StringComparison.OrdinalIgnoreCase);
                if (start < 0) return value;
                int end = value.IndexOf(']', start);
                if (end < 0) return value.Substring(0, start).TrimEnd();
                value = value.Remove(start, end - start + 1);
            }
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
                ShipCommandParsing.ParsePurchase(itemQuery, out itemQuery, out int quantity);

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

                int salePercent = 100;
                if (term.itemSalesPercentages != null && match < term.itemSalesPercentages.Length)
                    salePercent = Mathf.Clamp(term.itemSalesPercentages[match], 0, 100);
                int unitCost = (int)(list[match].creditsWorth * (salePercent / 100f));
                int totalCost = unitCost * quantity;
                if (term.groupCredits < totalCost)
                    return $"Need {totalCost} credits for {quantity} {matchName}, have {term.groupCredits}.";
                int dropshipCount = term.numberOfItemsInDropship + quantity;
                if (dropshipCount > 12)
                    return $"Dropship limit is 12 items; there are already {term.numberOfItemsInDropship} queued.";

                int[] bought = new int[quantity];
                for (int i = 0; i < bought.Length; i++) bought[i] = match;
                int newCredits = term.groupCredits - totalCost;
                term.BuyItemsServerRpc(bought, newCredits, dropshipCount);
                Plugin.Log?.LogInfo($"Bought {quantity}x store index {match} ({matchName}) for {totalCost}");
                return $"Bought {quantity} {matchName} for {totalCost} credits. {newCredits} left.";
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"BuyItem: {ex.Message}");
                return RunTerminalSentence("buy " + itemQuery);
            }
        }

        public static string BuildShipStatus(string query)
        {
            string lower = query?.ToLowerInvariant() ?? "status";
            bool full = lower == "status" || lower == "ship status" || lower == "report" || lower.Contains("full status");
            var sor = StartOfRound.Instance;
            var tod = TimeOfDay.Instance;
            var term = UnityEngine.Object.FindObjectOfType<Terminal>();
            var parts = new List<string>();

            try
            {
                if (full || lower.Contains("time") || lower.Contains("late"))
                {
                    if (sor != null && (sor.inShipPhase || !sor.shipHasLanded))
                        parts.Add("Time is paused in orbit");
                    else if (tod != null && HUDManager.Instance != null)
                        parts.Add("Time " + HUDManager.Instance.GetClockTimeFormatted(tod.normalizedTimeOfDay, tod.numberOfHours, false).Trim());
                    else if (tod != null)
                        parts.Add("Hour " + tod.hour);
                }

                if (full || lower.Contains("credit"))
                    parts.Add($"Credits {term?.groupCredits ?? 0}");

                if (full || lower.Contains("quota") || lower.Contains("deadline") || lower.Contains("days left"))
                {
                    if (tod != null)
                        parts.Add($"Quota {tod.quotaFulfilled}/{tod.profitQuota}, {Mathf.Max(0, tod.daysUntilDeadline)} days left");
                }

                if (full || lower.Contains("moon") || lower.Contains("where are we") || lower.Contains("weather"))
                {
                    string moon = sor?.currentLevel != null ? (sor.currentLevel.PlanetName ?? sor.currentLevel.name) : "unknown moon";
                    string weather = tod != null ? tod.currentLevelWeather.ToString() : "unknown weather";
                    parts.Add($"{moon}, {weather}");
                }

                if (full || lower.Contains("scrap"))
                {
                    int count = 0;
                    int value = 0;
                    foreach (var item in UnityEngine.Object.FindObjectsOfType<GrabbableObject>())
                    {
                        if (item?.itemProperties == null || !item.itemProperties.isScrap || !item.isInShipRoom) continue;
                        count++;
                        value += Mathf.Max(0, item.scrapValue);
                    }
                    parts.Add($"Ship scrap {count} items worth {value}");
                }

                if (full || lower.Contains("crew") || lower.Contains("alive"))
                {
                    int total = 0;
                    int alive = 0;
                    if (sor?.allPlayerScripts != null)
                        foreach (var player in sor.allPlayerScripts)
                            if (player != null && player.isPlayerControlled)
                            {
                                total++;
                                if (!player.isPlayerDead) alive++;
                            }
                    parts.Add($"Crew {alive}/{total} alive");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"BuildShipStatus: {ex.Message}");
            }

            return parts.Count == 0 ? "No ship status available." : string.Join(". ", parts) + ".";
        }

        public static string SetFacilityObject(string code, bool enable)
        {
            if (string.IsNullOrWhiteSpace(code)) return "Which terminal code?";
            var term = UnityEngine.Object.FindObjectOfType<Terminal>();
            if (term == null) return "No terminal.";

            TerminalAccessibleObject match = null;
            foreach (var candidate in UnityEngine.Object.FindObjectsOfType<TerminalAccessibleObject>())
            {
                if (candidate != null && string.Equals(candidate.objectCode, code, StringComparison.OrdinalIgnoreCase))
                {
                    match = candidate;
                    break;
                }
            }
            if (match == null) return $"No terminal object has code {code.ToUpperInvariant()}.";
            if (match.inCooldown) return $"Code {match.objectCode.ToUpperInvariant()} is cooling down.";

            string kind = DescribeFacilityObject(match);
            bool current = match.isBigDoor ? match.isDoorOpen : match.isPoweredOn;
            if (current == enable)
                return $"{kind} {match.objectCode.ToUpperInvariant()} is already {(enable ? "on/open" : "off/closed")}.";

            term.CallFunctionInAccessibleTerminalObject(match.objectCode);
            Plugin.Log?.LogInfo($"Buddy terminal code {match.objectCode}: {kind} -> {(enable ? "enabled/open" : "disabled/closed")}");
            return $"{(enable ? "Enabled/opened" : "Disabled/closed")} {kind} {match.objectCode.ToUpperInvariant()}.";
        }

        private static string DescribeFacilityObject(TerminalAccessibleObject accessible)
        {
            if (accessible == null) return "terminal object";
            if (accessible.isBigDoor) return "door";
            string name = ((accessible.mapRadarObject != null ? accessible.mapRadarObject.name : accessible.gameObject.name) ?? "").ToLowerInvariant();
            if (name.Contains("turret")) return "turret";
            if (name.Contains("mine")) return "landmine";
            return "facility hazard";
        }

        public static string SetHangarDoor(bool open)
        {
            var door = UnityEngine.Object.FindObjectOfType<HangarShipDoor>();
            if (door == null) return "No ship door controller.";
            if (!door.buttonsEnabled) return "Ship door controls are disabled right now.";
            if (open && door.overheated) return "Ship door hydraulics are overheated.";
            if (open && door.doorPower <= 0f) return "Ship door has no hydraulic power left.";

            if (open) door.SetDoorOpen();
            else door.SetDoorClosed();
            Plugin.Log?.LogInfo($"Buddy set hangar door {(open ? "open" : "closed")}.");
            return open ? "Opening the ship doors." : "Closing the ship doors.";
        }

        public static string SetShipLights(bool on)
        {
            var lights = StartOfRound.Instance?.shipRoomLights;
            if (lights == null) return "No ship light controller.";
            if (lights.areLightsOn == on) return $"Ship lights are already {(on ? "on" : "off")}.";
            lights.SetShipLightsServerRpc(on);
            Plugin.Log?.LogInfo($"Buddy set ship lights {(on ? "on" : "off")}.");
            return $"Ship lights {(on ? "on" : "off")}.";
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
