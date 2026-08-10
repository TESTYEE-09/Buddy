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
            sb.AppendLine("[SENSOR — ONLY REAL DATA. Do NOT invent anything not listed here.]");

            try
            {
                var sor = StartOfRound.Instance;
                if (sor == null)
                {
                    sb.AppendLine("Phase: unknown (no StartOfRound).");
                    return sb.ToString();
                }

                bool inSpace = sor.inShipPhase || !sor.shipHasLanded;
                sb.Append("Phase: ").Append(inSpace ? "IN SPACE / ORBIT (ship, terminal available)" : "ON MOON (landed)").AppendLine(".");

                string moon = sor.currentLevel != null
                    ? (sor.currentLevel.PlanetName ?? sor.currentLevel.name)
                    : "unknown";
                sb.Append("Current route/moon: ").Append(moon).AppendLine(".");

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
                        sb.Append("Company credits: ").Append(term.groupCredits).AppendLine(".");
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
                    sb.Append("Ship scrap: ").Append(shipScrapCount).Append(" items worth ")
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
                sb.Append("Sensor origin: ").Append(perspectiveName).AppendLine(". Distances below are from this position.");

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
                sb.Append("Crew status: ").Append(crew.Count == 0 ? "unknown" : string.Join(", ", crew)).AppendLine(".");

                if (buddy?.Enemy != null)
                {
                    string area = buddy.Enemy.isOutside ? "outside" : IsInsideShip(buddy.Enemy.transform.position, sor) ? "ship" : "facility";
                    sb.Append("Buddy location: ").Append(area);
                    if (perspective != null)
                        sb.Append(", ").Append(Vector3.Distance(perspective.transform.position, buddy.Enemy.transform.position).ToString("F0")).Append("m from ").Append(perspectiveName);
                    sb.AppendLine(".");
                }
                else if (inSpace)
                {
                    sb.AppendLine("Buddy location: voice terminal in the ship; no physical body in orbit.");
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
                    sb.AppendLine("Nearby entities (35m): NONE. You must NOT claim to see any monster.");
                else
                {
                    sb.Append("Nearby entities (35m): ");
                    sb.Append(string.Join(", ", nearby));
                    sb.AppendLine(".");
                    sb.AppendLine("You may only name entities from this list if talking about threats.");
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
                    sb.Append("Buddy AI state: ").Append(buddy.State).AppendLine(".");

                // Exits, doors, placed hazards, weather detail and unusual entity arrangements.
                BuddyEnvironmentSensors.AppendContext(sb, origin);
            }
            catch (Exception ex)
            {
                sb.Append("Sensor error: ").Append(ex.Message);
            }

            sb.AppendLine("[END SENSOR]");
            return sb.ToString();
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
