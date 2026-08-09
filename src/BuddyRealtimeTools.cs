using System;

namespace LethalAICrewmate
{
    /// <summary>
    /// Executes the small set of in-game functions exposed to the Realtime model. These functions
    /// never touch the filesystem, launch processes, access arbitrary URLs, or expose credentials.
    /// The model decides when to call them; host game code performs the requested action and returns
    /// the real result for Buddy to speak.
    /// </summary>
    internal static class BuddyRealtimeTools
    {
        internal static string Execute(string name, string arguments, int playerId)
        {
            try
            {
                switch (name)
                {
                    case "move_buddy":
                        return CrewmateAI.ExecuteToolAction(
                            JsonString(arguments, "action"),
                            playerId,
                            JsonFloat(arguments, "distance_metres", 10f),
                            JsonBool(arguments, "bring_to_player", false));
                    case "get_ship_status":
                        return TerminalBuddy.BuildShipStatus(JsonString(arguments, "topic") ?? "status");
                    case "list_moons":
                        return TerminalBuddy.ListMoons();
                    case "show_store":
                        return TerminalBuddy.ShowCreditsAndStoreHint();
                    case "route_moon":
                        return TerminalBuddy.RouteMoon(JsonString(arguments, "moon"));
                    case "buy_item":
                        return TerminalBuddy.BuyItem(
                            JsonString(arguments, "item"),
                            JsonInt(arguments, "quantity", 1),
                            playerId);
                    case "control_facility_object":
                        return TerminalBuddy.SetFacilityObject(
                            JsonString(arguments, "code"),
                            JsonBool(arguments, "enabled", false),
                            JsonString(arguments, "kind"));
                    case "set_hangar_doors":
                        return TerminalBuddy.SetHangarDoor(JsonBool(arguments, "open", false));
                    case "set_ship_lights":
                        return TerminalBuddy.SetShipLights(JsonBool(arguments, "on", false));
                    case "spawn_item":
                        return TerminalBuddy.SpawnItemInFront(
                            JsonString(arguments, "item"),
                            JsonInt(arguments, "quantity", 1),
                            playerId);
                    default:
                        return "Tool failed: unknown Buddy action '" + (name ?? "") + "'.";
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning("Realtime tool '" + (name ?? "") + "' failed: " + ex.Message);
                return "Tool failed: the game rejected that action.";
            }
        }

        private static string JsonString(string json, string key)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key)) return null;
            int keyAt = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
            if (keyAt < 0) return null;
            int colon = json.IndexOf(':', keyAt + key.Length + 2);
            if (colon < 0) return null;
            int at = colon + 1;
            while (at < json.Length && char.IsWhiteSpace(json[at])) at++;
            if (at >= json.Length || json[at++] != '"') return null;
            var value = new System.Text.StringBuilder();
            while (at < json.Length)
            {
                char c = json[at++];
                if (c == '"') break;
                if (c != '\\' || at >= json.Length) { value.Append(c); continue; }
                char escaped = json[at++];
                if (escaped == 'n' || escaped == 'r') value.Append(' ');
                else if (escaped == 't') value.Append('\t');
                else value.Append(escaped);
            }
            return value.ToString().Trim();
        }

        private static int JsonInt(string json, string key, int fallback)
        {
            string token = JsonScalar(json, key);
            return int.TryParse(token, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out int value) ? value : fallback;
        }

        private static float JsonFloat(string json, string key, float fallback)
        {
            string token = JsonScalar(json, key);
            return float.TryParse(token, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float value) ? value : fallback;
        }

        private static bool JsonBool(string json, string key, bool fallback)
        {
            string token = JsonScalar(json, key);
            return bool.TryParse(token, out bool value) ? value : fallback;
        }

        private static string JsonScalar(string json, string key)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key)) return null;
            int keyAt = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
            if (keyAt < 0) return null;
            int colon = json.IndexOf(':', keyAt + key.Length + 2);
            if (colon < 0) return null;
            int at = colon + 1;
            while (at < json.Length && char.IsWhiteSpace(json[at])) at++;
            int end = at;
            while (end < json.Length && json[end] != ',' && json[end] != '}' && !char.IsWhiteSpace(json[end])) end++;
            return end > at ? json.Substring(at, end - at).Trim() : null;
        }
    }
}
