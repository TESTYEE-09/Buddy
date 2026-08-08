using System;

namespace LethalAICrewmate
{
    /// <summary>
    /// Keeps newly entered Groq keys in process memory by default. An environment variable is the
    /// preferred persistent source; writing a key to BepInEx config requires an explicit opt-in.
    /// </summary>
    internal static class GroqSecrets
    {
        private const string EnvironmentVariable = "LETHAL_AI_GROQ_API_KEY";
        private static string _sessionKey = "";

        internal static string CurrentKey
        {
            get
            {
                string environmentKey = Normalize(Environment.GetEnvironmentVariable(EnvironmentVariable));
                if (!string.IsNullOrEmpty(environmentKey)) return environmentKey;
                if (!string.IsNullOrEmpty(_sessionKey)) return _sessionKey;
                return Normalize(Plugin.ApiKey?.Value);
            }
        }

        internal static bool HasKey => !string.IsNullOrEmpty(CurrentKey);

        internal static bool SetFromMenu(string key)
        {
            key = Normalize(key);
            if (string.IsNullOrEmpty(key)) return false;

            _sessionKey = key;
            if (Plugin.PersistApiKey != null && Plugin.PersistApiKey.Value && Plugin.ApiKey != null)
            {
                Plugin.ApiKey.Value = key;
                Plugin.Instance?.Config.Save();
            }
            return true;
        }

        internal static void ClearMenuKey()
        {
            _sessionKey = "";
            if (Plugin.ApiKey != null)
            {
                Plugin.ApiKey.Value = "";
                Plugin.Instance?.Config.Save();
            }
        }

        private static string Normalize(string value)
        {
            string key = (value ?? "").Trim();
            if (key.Length > 256) return "";
            return key;
        }
    }
}
