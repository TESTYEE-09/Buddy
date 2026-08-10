using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using GameNetcodeStuff;
using UnityEngine;

namespace LethalAICrewmate
{
    /// <summary>
    /// Extra host-side environmental truth: exits, doors, placed hazards, weather and unusual
    /// enemy situations. Everything reported here is measured from live scene objects, so Buddy
    /// still cannot invent a threat. Unprompted reactions are deliberately rare — the sensor text
    /// is cheap, but the autonomy events it raises carry long per-kind cooldowns.
    /// </summary>
    internal static class BuddyEnvironmentSensors
    {
        private const float ScanRadius = 30f;
        private const float PollSeconds = 3f;

        private static readonly Dictionary<string, bool> BoolFieldMissing = new Dictionary<string, bool>();
        private static float _nextPollAt;
        private static int _lastReportedHazardId;
        private static string _lastWeather;
        private static bool _weatherKnown;
        private static int _lastUnusualEnemyId;

        internal static bool Active => Plugin.EnvironmentAwareness?.Value == true;

        /// <summary>Appends confirmed environment detail to the live sensor block.</summary>
        internal static void AppendContext(StringBuilder sb, Vector3 origin)
        {
            if (!Active || sb == null) return;
            try
            {
                AppendExit(sb, origin);
                AppendDoors(sb, origin);
                AppendHazards(sb, origin);
                AppendWeatherAdvice(sb);
                AppendUnusualEnemies(sb, origin);
            }
            catch (Exception ex)
            {
                sb.Append("Some surroundings unavailable: ").Append(ex.Message).AppendLine();
            }
        }

        internal static void Tick()
        {
            try
            {
                if (!Active || !CrewmateSpawner.IsHost()) return;
                if (Time.unscaledTime < _nextPollAt) return;
                _nextPollAt = Time.unscaledTime + PollSeconds;

                CrewmateData data = CrewmateRegistry.GetPrimary();
                if (data?.Enemy == null) return;
                Vector3 origin = data.Enemy.transform.position;

                NoteHazard(origin);
                NoteWeatherChange();
                NoteUnusualEnemy(origin);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning("Buddy environment sensors: " + ex.Message);
            }
        }

        // ---------- sensor text ----------

        private static void AppendExit(StringBuilder sb, Vector3 origin)
        {
            EntranceTeleport nearest = null;
            float best = float.MaxValue;
            foreach (EntranceTeleport entrance in UnityEngine.Object.FindObjectsOfType<EntranceTeleport>())
            {
                if (entrance == null) continue;
                float distance = Vector3.Distance(origin, entrance.transform.position);
                if (distance < best) { best = distance; nearest = entrance; }
            }
            if (nearest == null || best > 120f)
            {
                sb.AppendLine("Nearest way out: not visible from here.");
                return;
            }
            bool toBuilding = TryReadBool(nearest, "isEntranceToBuilding", out bool value) && value;
            sb.Append("Nearest ").Append(toBuilding ? "facility entrance" : "way out")
              .Append(": ").Append(Mathf.RoundToInt(best)).AppendLine(" metres away.");
        }

        private static void AppendDoors(StringBuilder sb, Vector3 origin)
        {
            int closed = 0;
            int locked = 0;
            foreach (DoorLock door in UnityEngine.Object.FindObjectsOfType<DoorLock>())
            {
                if (door == null) continue;
                if (Vector3.Distance(origin, door.transform.position) > ScanRadius) continue;
                if (TryReadBool(door, "isDoorOpened", out bool open) && open) continue;
                closed++;
                if (TryReadBool(door, "isLocked", out bool isLocked) && isLocked) locked++;
            }
            if (closed == 0)
            {
                sb.AppendLine("Doors within 30m: none closed.");
                return;
            }
            sb.Append("Doors within 30m: ").Append(closed).Append(" closed");
            if (locked > 0) sb.Append(", ").Append(locked).Append(" of them locked");
            sb.AppendLine(".");
        }

        private static void AppendHazards(StringBuilder sb, Vector3 origin)
        {
            var hazards = new List<string>();
            foreach (Turret turret in UnityEngine.Object.FindObjectsOfType<Turret>())
            {
                if (turret == null) continue;
                float distance = Vector3.Distance(origin, turret.transform.position);
                if (distance <= ScanRadius) hazards.Add("turret (" + Mathf.RoundToInt(distance) + "m)");
            }
            foreach (Landmine mine in UnityEngine.Object.FindObjectsOfType<Landmine>())
            {
                if (mine == null) continue;
                if (TryReadBool(mine, "hasExploded", out bool spent) && spent) continue;
                float distance = Vector3.Distance(origin, mine.transform.position);
                if (distance <= ScanRadius) hazards.Add("landmine (" + Mathf.RoundToInt(distance) + "m)");
            }

            if (hazards.Count == 0)
            {
                sb.AppendLine("Traps within 30m: none.");
                return;
            }
            if (hazards.Count > 6) hazards.RemoveRange(6, hazards.Count - 6);
            sb.Append("Traps within 30m: ").Append(string.Join(", ", hazards)).AppendLine(".");
        }

        private static void AppendWeatherAdvice(StringBuilder sb)
        {
            string weather = CurrentWeatherName();
            if (string.IsNullOrEmpty(weather)) return;
            sb.Append("Weather: ").Append(weather);
            string advice = WeatherAdvice(weather);
            if (!string.IsNullOrEmpty(advice)) sb.Append(" — ").Append(advice);
            sb.AppendLine(".");
        }

        private static void AppendUnusualEnemies(StringBuilder sb, Vector3 origin)
        {
            string unusual = DescribeUnusualEnemy(origin, out _);
            sb.AppendLine(string.IsNullOrEmpty(unusual)
                ? "Anything odd: nothing."
                : "Something odd: " + unusual + ".");
        }

        // ---------- rare unprompted reactions ----------

        private static void NoteHazard(Vector3 origin)
        {
            Component nearest = null;
            string label = null;
            float best = float.MaxValue;

            foreach (Turret turret in UnityEngine.Object.FindObjectsOfType<Turret>())
            {
                if (turret == null) continue;
                float distance = Vector3.Distance(origin, turret.transform.position);
                if (distance < best && distance <= 10f) { best = distance; nearest = turret; label = "a turret"; }
            }
            foreach (Landmine mine in UnityEngine.Object.FindObjectsOfType<Landmine>())
            {
                if (mine == null) continue;
                if (TryReadBool(mine, "hasExploded", out bool spent) && spent) continue;
                float distance = Vector3.Distance(origin, mine.transform.position);
                if (distance < best && distance <= 7f) { best = distance; nearest = mine; label = "a live landmine"; }
            }

            if (nearest == null) return;
            int id = nearest.GetInstanceID();
            if (id == _lastReportedHazardId) return;
            _lastReportedHazardId = id;
            BuddyAutonomy.Queue(BuddyContextEvent.HazardNearby,
                "Buddy has just come within " + Mathf.RoundToInt(best) + " metres of " + label +
                " that the crew may not have noticed. Mention it once, plainly, and only if it is still relevant.");
        }

        private static void NoteWeatherChange()
        {
            string weather = CurrentWeatherName();
            if (string.IsNullOrEmpty(weather)) return;
            if (!_weatherKnown)
            {
                _weatherKnown = true;
                _lastWeather = weather;
                return;
            }
            if (string.Equals(weather, _lastWeather, StringComparison.Ordinal)) return;
            _lastWeather = weather;
            BuddyAutonomy.Queue(BuddyContextEvent.WeatherTurn,
                "The confirmed weather on this moon has changed to " + weather +
                ". Say one short practical thing about working in it, or nothing.");
        }

        private static void NoteUnusualEnemy(Vector3 origin)
        {
            string description = DescribeUnusualEnemy(origin, out int id);
            if (string.IsNullOrEmpty(description) || id == _lastUnusualEnemyId) return;
            _lastUnusualEnemyId = id;
            BuddyAutonomy.Queue(BuddyContextEvent.UnusualEnemy,
                "Confirmed unusual entity situation near Buddy: " + description +
                ". Give one short, useful warning. Do not embellish or add details Buddy cannot see.");
        }

        /// <summary>
        /// Only genuinely notable arrangements, never a running commentary on ordinary wildlife.
        /// </summary>
        private static string DescribeUnusualEnemy(Vector3 origin, out int instanceId)
        {
            instanceId = 0;
            try
            {
                int hostiles = 0;
                EnemyAI crowdSample = null;
                EnemyAI stalker = null;
                float stalkerDistance = float.MaxValue;

                PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
                foreach (EnemyAI enemy in UnityEngine.Object.FindObjectsOfType<EnemyAI>())
                {
                    if (enemy == null || enemy.isEnemyDead) continue;
                    if (CrewmateRegistry.IsCrewmate(enemy)) continue;
                    float distance = Vector3.Distance(origin, enemy.transform.position);
                    if (distance > ScanRadius) continue;
                    hostiles++;
                    if (crowdSample == null) crowdSample = enemy;

                    if (players == null) continue;
                    foreach (PlayerControllerB player in players)
                    {
                        if (player == null || !player.isPlayerControlled || player.isPlayerDead) continue;
                        float toPlayer = Vector3.Distance(player.transform.position, enemy.transform.position);
                        if (toPlayer > 9f || toPlayer >= stalkerDistance) continue;
                        Vector3 toEnemy = enemy.transform.position - player.transform.position;
                        toEnemy.y = 0f;
                        if (toEnemy.sqrMagnitude < 0.05f) continue;
                        // Behind the player's facing, so they are very unlikely to have seen it.
                        if (Vector3.Dot(player.transform.forward, toEnemy.normalized) > -0.35f) continue;
                        stalker = enemy;
                        stalkerDistance = toPlayer;
                    }
                }

                if (stalker != null)
                {
                    instanceId = stalker.GetInstanceID();
                    return EnemyName(stalker) + " is roughly " + Mathf.RoundToInt(stalkerDistance) +
                           " metres behind a crewmate who is facing away from it";
                }
                if (hostiles >= 3 && crowdSample != null)
                {
                    instanceId = crowdSample.GetInstanceID() ^ hostiles;
                    return hostiles + " separate entities are inside 30 metres at once";
                }
            }
            catch { /* ignore */ }
            return null;
        }

        private static string EnemyName(EnemyAI enemy)
        {
            try
            {
                if (enemy?.enemyType != null && !string.IsNullOrWhiteSpace(enemy.enemyType.enemyName))
                    return enemy.enemyType.enemyName;
            }
            catch { /* ignore */ }
            return "An entity";
        }

        private static string CurrentWeatherName()
        {
            try
            {
                if (TimeOfDay.Instance == null) return null;
                return TimeOfDay.Instance.currentLevelWeather.ToString();
            }
            catch { return null; }
        }

        private static string WeatherAdvice(string weather)
        {
            if (string.IsNullOrEmpty(weather)) return null;
            string key = weather.ToLowerInvariant();
            if (key.Contains("stormy")) return "metal in hand draws lightning outside";
            if (key.Contains("flood")) return "the water outside keeps rising";
            if (key.Contains("eclipsed")) return "far more entities than normal will be out";
            if (key.Contains("foggy")) return "visibility outside is very poor";
            if (key.Contains("rainy")) return "quicksand mud outside";
            return null;
        }

        /// <summary>
        /// Vanilla field names have moved between game versions, so each unknown field is probed
        /// once and then remembered as missing instead of throwing every frame.
        /// </summary>
        private static bool TryReadBool(object target, string fieldName, out bool value)
        {
            value = false;
            if (target == null || string.IsNullOrEmpty(fieldName)) return false;
            string cacheKey = target.GetType().FullName + "." + fieldName;
            if (BoolFieldMissing.TryGetValue(cacheKey, out bool missing) && missing) return false;
            try
            {
                FieldInfo field = target.GetType().GetField(fieldName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field == null || field.FieldType != typeof(bool))
                {
                    BoolFieldMissing[cacheKey] = true;
                    return false;
                }
                value = (bool)field.GetValue(target);
                BoolFieldMissing[cacheKey] = false;
                return true;
            }
            catch
            {
                BoolFieldMissing[cacheKey] = true;
                return false;
            }
        }

        internal static void ResetSession()
        {
            _nextPollAt = 0f;
            _lastReportedHazardId = 0;
            _lastWeather = null;
            _weatherKnown = false;
            _lastUnusualEnemyId = 0;
        }
    }
}
