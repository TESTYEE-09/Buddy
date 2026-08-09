using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using GameNetcodeStuff;
using Unity.Netcode;
using UnityEngine;

namespace LethalAICrewmate
{
    /// <summary>
    /// Host-side terminal / routing helpers — mainly while in orbit (inShipPhase).
    /// </summary>
    public static class TerminalBuddy
    {
        private const int MaxSpawnedObjectsPerRound = 12;
        private static int _spawnRoundSeed = int.MinValue;
        private static int _spawnedThisRound;

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

        public static string SpawnItemInFront(string query, int quantity, int requestingPlayerId)
        {
            if (!CrewmateSpawner.IsHost()) return "Only the host can spawn objects.";
            var sor = StartOfRound.Instance;
            var round = RoundManager.Instance;
            if (sor?.allItemsList?.itemsList == null || round == null)
                return "Object list isn't ready yet.";

            int seed = sor.randomMapSeed;
            if (_spawnRoundSeed != seed)
            {
                _spawnRoundSeed = seed;
                _spawnedThisRound = 0;
            }
            quantity = Mathf.Clamp(quantity, 1, 3);
            if (_spawnedThisRound + quantity > MaxSpawnedObjectsPerRound)
                return $"Spawn limit is {MaxSpawnedObjectsPerRound} objects per round. Even magic has paperwork.";

            PlayerControllerB player = ResolvePlayer(requestingPlayerId);
            if (player == null) return "I can't find who asked for that.";

            string wanted = NormalizeItemName(query);
            Item found = null;
            foreach (Item candidate in sor.allItemsList.itemsList)
            {
                if (candidate == null || candidate.spawnPrefab == null || string.IsNullOrWhiteSpace(candidate.itemName)) continue;
                string candidateName = NormalizeItemName(candidate.itemName);
                if (candidateName == wanted || candidateName.Contains(wanted) || wanted.Contains(candidateName))
                {
                    found = candidate;
                    break;
                }
            }
            if (found == null) return $"I don't recognize a safe item named '{query}'.";

            Vector3 forward = player.transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.01f) forward = Vector3.forward;
            forward.Normalize();
            Vector3 basePosition = player.transform.position + forward * 1.8f + Vector3.up * 0.35f;

            int spawned = 0;
            for (int i = 0; i < quantity; i++)
            {
                try
                {
                    Vector3 side = player.transform.right * ((i - (quantity - 1) * 0.5f) * 0.55f);
                    GameObject go = UnityEngine.Object.Instantiate(found.spawnPrefab, basePosition + side,
                        Quaternion.identity, round.spawnedScrapContainer);
                    var grabbable = go.GetComponent<GrabbableObject>();
                    var networkObject = go.GetComponent<NetworkObject>();
                    if (grabbable == null || networkObject == null)
                    {
                        UnityEngine.Object.Destroy(go);
                        continue;
                    }
                    grabbable.fallTime = 0f;
                    networkObject.Spawn(true);
                    spawned++;
                }
                catch (Exception ex)
                {
                    Plugin.Log?.LogWarning($"Item spawn failed for '{found.itemName}': {ex.Message}");
                }
            }

            _spawnedThisRound += spawned;
            if (spawned == 0) return $"Couldn't safely spawn {found.itemName}.";
            return $"Spawned {spawned} {found.itemName} in front of {player.playerUsername}.";
        }

        private static PlayerControllerB ResolvePlayer(int playerId)
        {
            var players = StartOfRound.Instance?.allPlayerScripts;
            if (players == null) return null;
            foreach (var player in players)
                if (player != null && (int)player.playerClientId == playerId) return player;
            return playerId >= 0 && playerId < players.Length ? players[playerId] : null;
        }

        private static string NormalizeItemName(string value)
        {
            string clean = (value ?? "").ToLowerInvariant().Trim();
            clean = Regex.Replace(clean, @"^(?:an?|the|some)\s+", "", RegexOptions.IgnoreCase);
            clean = Regex.Replace(clean, @"\b(?:item|object|thing)\b", "", RegexOptions.IgnoreCase);
            clean = Regex.Replace(clean, @"[^a-z0-9]+", "");
            if (clean.Length > 3 && clean.EndsWith("s", StringComparison.Ordinal))
                clean = clean.Substring(0, clean.Length - 1);
            return clean;
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
                string n = (lvl.PlanetName ?? lvl.name ?? "").ToLowerInvariant().Trim();
                if (string.IsNullOrEmpty(n)) continue;
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
                return "Routing failed safely; use the terminal manually.";
            }
        }

        public static string BuyItem(string itemQuery, int quantity)
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
                itemQuery = itemQuery.Trim().ToLowerInvariant();
                quantity = Mathf.Clamp(quantity, 1, 12);

                // buyableItemsList is Item[] on Terminal
                var list = term.buyableItemsList;
                if (list == null || list.Length == 0)
                    return "Store data is unavailable; use the terminal manually.";

                int match = -1;
                string matchName = null;
                for (int i = 0; i < list.Length; i++)
                {
                    var item = list[i];
                    if (item == null) continue;
                    string n = (item.itemName ?? "").ToLowerInvariant().Trim();
                    if (string.IsNullOrEmpty(n)) continue;
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
                return "Purchase failed safely; use the terminal manually.";
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

        public static string SetFacilityObject(string code, bool enable, string expectedKind = null)
        {
            var term = UnityEngine.Object.FindObjectOfType<Terminal>();
            if (term == null) return "No terminal.";

            TerminalAccessibleObject match = null;
            var candidates = UnityEngine.Object.FindObjectsOfType<TerminalAccessibleObject>();
            foreach (var candidate in candidates)
            {
                if (candidate != null && !string.IsNullOrWhiteSpace(code) &&
                    string.Equals(candidate.objectCode, code, StringComparison.OrdinalIgnoreCase))
                {
                    match = candidate;
                    break;
                }
            }
            if (match == null && !string.IsNullOrWhiteSpace(code))
                return $"No terminal object has code {code.ToUpperInvariant()}.";

            if (match == null && !string.IsNullOrWhiteSpace(expectedKind))
            {
                var kindMatches = new List<TerminalAccessibleObject>();
                foreach (var candidate in candidates)
                    if (candidate != null && string.Equals(DescribeFacilityObject(candidate), expectedKind, StringComparison.OrdinalIgnoreCase))
                        kindMatches.Add(candidate);

                if (kindMatches.Count == 1)
                    match = kindMatches[0];
                else if (kindMatches.Count == 0)
                    return $"No terminal-controlled {expectedKind} is currently available.";
                else
                {
                    var codes = new List<string>();
                    foreach (var candidate in kindMatches)
                        if (!string.IsNullOrWhiteSpace(candidate.objectCode)) codes.Add(candidate.objectCode.ToUpperInvariant());
                    return $"Which {expectedKind}? Available codes: {string.Join(", ", codes)}.";
                }
            }

            if (match == null) return "Which terminal code?";
            if (match.inCooldown) return $"Code {match.objectCode.ToUpperInvariant()} is cooling down.";

            string kind = DescribeFacilityObject(match);
            if (!string.IsNullOrWhiteSpace(expectedKind) &&
                !string.Equals(kind, expectedKind, StringComparison.OrdinalIgnoreCase))
                return $"Code {match.objectCode.ToUpperInvariant()} controls a {kind}, not a {expectedKind}.";

            bool current = match.isBigDoor ? match.isDoorOpen : match.isPoweredOn;
            if (current == enable)
                return $"{kind} {match.objectCode.ToUpperInvariant()} is already {(enable ? "on/open" : "off/closed")}.";

            term.CallFunctionInAccessibleTerminalObject(match.objectCode);
            Plugin.Log?.LogInfo($"Buddy terminal code {match.objectCode}: {kind} -> {(enable ? "enabled/open" : "disabled/closed")}");
            return $"Terminal command sent: {(enable ? "enable/open" : "disable/close")} {kind} {match.objectCode.ToUpperInvariant()}.";
        }

        private static string DescribeFacilityObject(TerminalAccessibleObject accessible)
        {
            if (accessible == null) return "terminal object";
            if (accessible.isBigDoor) return "door";
            if (accessible.GetComponentInParent<Turret>() != null) return "turret";
            if (accessible.GetComponentInParent<Landmine>() != null) return "landmine";
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
            if (term == null) return "Store data is unavailable.";
            var items = term.buyableItemsList;
            if (items == null || items.Length == 0)
                return $"Current ship credits: {term.groupCredits}. Store data is unavailable.";

            var available = new List<string>();
            for (int i = 0; i < items.Length && available.Count < 12; i++)
            {
                Item item = items[i];
                if (item == null || string.IsNullOrWhiteSpace(item.itemName)) continue;
                int salePercent = term.itemSalesPercentages != null && i < term.itemSalesPercentages.Length
                    ? Mathf.Clamp(term.itemSalesPercentages[i], 0, 100)
                    : 100;
                int price = (int)(item.creditsWorth * (salePercent / 100f));
                available.Add($"{item.itemName} {price}");
            }
            return $"Current ship credits: {term.groupCredits}. Store: {string.Join(", ", available)}.";
        }
    }
}
