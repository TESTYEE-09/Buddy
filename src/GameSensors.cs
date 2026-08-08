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
        public static string BuildLiveContext()
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
                if (buddy?.Enemy != null)
                    origin = buddy.Enemy.transform.position;
                else if (sor.localPlayerController != null)
                    origin = sor.localPlayerController.transform.position;

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

                int scrapNear = 0;
                try
                {
                    foreach (var g in UnityEngine.Object.FindObjectsOfType<GrabbableObject>())
                    {
                        if (g?.itemProperties == null || !g.itemProperties.isScrap) continue;
                        if (g.isHeld || g.isInShipRoom) continue;
                        if (Vector3.Distance(origin, g.transform.position) <= 25f)
                            scrapNear++;
                    }
                }
                catch { /* ignore */ }
                sb.Append("Loose scrap within 25m: ").Append(scrapNear).AppendLine(".");

                if (buddy != null)
                    sb.Append("Buddy AI state: ").Append(buddy.State).AppendLine(".");
            }
            catch (Exception ex)
            {
                sb.Append("Sensor error: ").Append(ex.Message);
            }

            sb.AppendLine("[END SENSOR]");
            return sb.ToString();
        }
    }
}
