using System;
using System.Collections.Generic;
using System.Text;
using GameNetcodeStuff;
using UnityEngine;

namespace LethalAICrewmate
{
    /// <summary>
    /// Live host-side game truth for the LLM so Buddy cannot invent nearby threats.
    /// </summary>
    public static class GameSensors
    {
        public static string BuildLiveContext(int perspectivePlayerId = -1)
        {
            var sb = new StringBuilder(512);
            sb.AppendLine("RIGHT NOW");

            try
            {
                var sor = StartOfRound.Instance;
                if (sor == null)
                {
                    sb.AppendLine("Where: unknown.");
                    return sb.ToString();
                }

                bool inSpace = sor.inShipPhase || !sor.shipHasLanded;
                sb.Append("Where: ").Append(inSpace ? "in orbit, aboard the ship" : "landed on the moon").AppendLine(".");

                string moon = sor.currentLevel != null
                    ? (sor.currentLevel.PlanetName ?? sor.currentLevel.name)
                    : "unknown";
                sb.Append("Moon: ").Append(moon).AppendLine(".");

                try
                {
                    if (TimeOfDay.Instance != null)
                        sb.Append("Time: ").Append(TimeOfDay.Instance.dayMode)
                          .Append(" hour ").Append(TimeOfDay.Instance.hour).AppendLine(".");
                }
                catch { /* ignore */ }

                try
                {
                    var term = UnityEngine.Object.FindObjectOfType<Terminal>();
                    if (term != null)
                        sb.Append("Credits: ").Append(term.groupCredits).AppendLine(".");
                }
                catch { /* ignore */ }

                try
                {
                    if (TimeOfDay.Instance != null)
                    {
                        sb.Append("Quota: ").Append(TimeOfDay.Instance.quotaFulfilled)
                          .Append('/').Append(TimeOfDay.Instance.profitQuota)
                          .Append("; days left: ").Append(Mathf.Max(0, TimeOfDay.Instance.daysUntilDeadline))
                          .Append("; weather: ").Append(TimeOfDay.Instance.currentLevelWeather).AppendLine(".");
                    }
                }
                catch { /* ignore */ }

                try
                {
                    int shipScrapCount = 0;
                    int shipScrapValue = 0;
                    foreach (var item in UnityEngine.Object.FindObjectsOfType<GrabbableObject>())
                    {
                        if (item?.itemProperties == null || !item.itemProperties.isScrap || !item.isInShipRoom) continue;
                        shipScrapCount++;
                        shipScrapValue += Mathf.Max(0, item.scrapValue);
                    }
                    sb.Append("Scrap aboard the ship: ").Append(shipScrapCount).Append(" items worth ")
                      .Append(shipScrapValue).AppendLine(".");
                }
                catch { /* ignore */ }

                // Nearby enemies around Buddy or host player
                Vector3 origin = Vector3.zero;
                var buddy = CrewmateRegistry.GetPrimary();
                PlayerControllerB perspective = FindPlayer(perspectivePlayerId, sor.allPlayerScripts);
                if (perspective != null)
                    origin = perspective.transform.position;
                else if (buddy?.Enemy != null)
                    origin = buddy.Enemy.transform.position;
                else if (sor.localPlayerController != null)
                    origin = sor.localPlayerController.transform.position;

                string perspectiveName = perspective != null
                    ? PromptSafety.SanitizePlayerName(perspective.playerUsername)
                    : buddy?.Enemy != null ? "Buddy" : "host";
                sb.Append("Distances measured from: ").Append(perspectiveName).AppendLine(".");

                var crew = new List<string>();
                if (sor.allPlayerScripts != null)
                {
                    foreach (PlayerControllerB player in sor.allPlayerScripts)
                    {
                        if (player == null || string.IsNullOrWhiteSpace(player.playerUsername)) continue;
                        string playerName = PromptSafety.SanitizePlayerName(player.playerUsername);
                        crew.Add(playerName + "=" + (player.isPlayerDead ? "DEAD" : player.isPlayerControlled ? "alive" : "not active"));
                    }
                }
                sb.Append("Crew: ").Append(crew.Count == 0 ? "unknown" : string.Join(", ", crew)).AppendLine(".");

                if (buddy?.Enemy != null)
                {
                    string area = buddy.Enemy.isOutside ? "outside" : IsInsideShip(buddy.Enemy.transform.position, sor) ? "ship" : "facility";
                    sb.Append("You are: ").Append(area);
                    if (perspective != null)
                        sb.Append(", ").Append(Vector3.Distance(perspective.transform.position, buddy.Enemy.transform.position).ToString("F0")).Append("m from ").Append(perspectiveName);
                    sb.AppendLine(".");
                }
                else if (inSpace)
                {
                    sb.AppendLine("You are: a voice in the ship, no body while in orbit.");
                }

                var nearby = new List<string>();
                try
                {
                    foreach (var e in UnityEngine.Object.FindObjectsOfType<EnemyAI>())
                    {
                        if (e == null || e.isEnemyDead) continue;
                        if (CrewmateRegistry.IsCrewmate(e)) continue;
                        float d = Vector3.Distance(origin, e.transform.position);
                        if (d > 35f) continue;
                        string en = e.enemyType != null ? e.enemyType.enemyName : e.GetType().Name;
                        nearby.Add($"{en} ({d:F0}m)");
                    }
                }
                catch { /* ignore */ }

                if (nearby.Count == 0)
                    sb.AppendLine("Creatures within 35m: none.");
                else
                {
                    sb.Append("Creatures within 35m: ");
                    sb.Append(string.Join(", ", nearby));
                    sb.AppendLine(".");
                    
                }

                var scrapNear = new List<GrabbableObject>();
                try
                {
                    foreach (var g in UnityEngine.Object.FindObjectsOfType<GrabbableObject>())
                    {
                        if (g?.itemProperties == null || !g.itemProperties.isScrap) continue;
                        if (g.isHeld || g.isInShipRoom) continue;
                        if (Vector3.Distance(origin, g.transform.position) <= 25f)
                            scrapNear.Add(g);
                    }
                    scrapNear.Sort((a, b) => Vector3.Distance(origin, a.transform.position)
                        .CompareTo(Vector3.Distance(origin, b.transform.position)));
                }
                catch { /* ignore */ }

                if (scrapNear.Count == 0)
                {
                    sb.AppendLine("Loose scrap within 25m: NONE.");
                }
                else
                {
                    int shown = Mathf.Min(6, scrapNear.Count);
                    var parts = new List<string>(shown);
                    for (int i = 0; i < shown; i++)
                    {
                        GrabbableObject g = scrapNear[i];
                        string itemName = g.itemProperties.itemName ?? g.itemProperties.name ?? "scrap";
                        parts.Add(PromptSafety.SanitizeItemName(itemName) + " (" + g.scrapValue + "cr, "
                            + Vector3.Distance(origin, g.transform.position).ToString("F0") + "m)");
                    }
                    sb.Append("Loose scrap within 25m: ").Append(string.Join(", ", parts));
                    if (scrapNear.Count > shown)
                        sb.Append(", and ").Append(scrapNear.Count - shown).Append(" more further out");
                    sb.AppendLine(".");
                }

                if (buddy != null)
                    sb.Append("You are currently: ").Append(DescribeState(buddy.State)).AppendLine(".");

                // Exits, doors, placed hazards, weather detail and unusual entity arrangements.
                BuddyEnvironmentSensors.AppendContext(sb, origin);
            }
            catch (Exception ex)
            {
                sb.Append("Some readings unavailable: ").Append(ex.Message);
            }

            
            return sb.ToString();
        }

        /// <summary>
        /// Plain English for what Buddy is doing. The enum name leaked machine vocabulary into the
        /// prompt ("Buddy AI state: FollowOwner"), which the contract then had to spend rules
        /// forbidding him from repeating. Data he can read out as-is needs no such rule.
        /// </summary>
        private static string DescribeState(CrewmateState state)
        {
            switch (state)
            {
                case CrewmateState.Stay: return "holding still where you were left";
                case CrewmateState.ReturnToShip: return "heading back to the ship";
                case CrewmateState.FetchScrap: return "off fetching scrap";
                case CrewmateState.ScoutAhead: return "scouting ahead";
                default: return "following your owner";
            }
        }

        private static PlayerControllerB FindPlayer(int playerId, PlayerControllerB[] players)
        {
            if (playerId < 0 || players == null) return null;
            foreach (PlayerControllerB player in players)
                if (player != null && (int)player.playerClientId == playerId) return player;
            return playerId < players.Length ? players[playerId] : null;
        }

        private static bool IsInsideShip(Vector3 position, StartOfRound sor)
        {
            try
            {
                if (sor?.shipInnerRoomBounds != null) return sor.shipInnerRoomBounds.bounds.Contains(position);
                if (sor?.shipBounds != null) return sor.shipBounds.bounds.Contains(position);
            }
            catch { }
            return false;
        }
    }
}
