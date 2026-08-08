using System;

namespace LethalAICrewmate
{
    /// <summary>
    /// Keeps newly entered Groq keys in process memory by default. An environment variable is the
    /// preferred persistent source; writing a key to BepInEx config requires an explicit opt-in.
    /// </summary>
    internal static class GroqSecrets
    {
        private const string GroqEnvironmentVariable = "LETHAL_AI_GROQ_API_KEY";
        private const string OpenAiEnvironmentVariable = "LETHAL_AI_OPENAI_API_KEY";
        private static string _sessionKey = "";

        internal static bool IsOpenAi => string.Equals(Plugin.Provider?.Value?.Trim(), "OpenAI", StringComparison.OrdinalIgnoreCase);
        internal static string ProviderName => IsOpenAi ? "OpenAI" : "Groq";
        internal static string ChatEndpoint => IsOpenAi
            ? "https://api.openai.com/v1/chat/completions"
            : "https://api.groq.com/openai/v1/chat/completions";
        internal const string OpenAiResponsesEndpoint = "https://api.openai.com/v1/responses";
        internal static string SttEndpoint => IsOpenAi
            ? "https://api.openai.com/v1/audio/transcriptions"
            : "https://api.groq.com/openai/v1/audio/transcriptions";
        internal static string TtsEndpoint => IsOpenAi
            ? "https://api.openai.com/v1/audio/speech"
            : "https://api.groq.com/openai/v1/audio/speech";
        internal static string ModelsEndpoint => IsOpenAi
            ? "https://api.openai.com/v1/models"
            : "https://api.groq.com/openai/v1/models";

        internal static string CurrentKey
        {
            get
            {
                string environmentKey = Normalize(Environment.GetEnvironmentVariable(
                    IsOpenAi ? OpenAiEnvironmentVariable : GroqEnvironmentVariable));
                if (!string.IsNullOrEmpty(environmentKey)) return environmentKey;
                if (!string.IsNullOrEmpty(_sessionKey)) return _sessionKey;
                return Normalize(IsOpenAi ? Plugin.OpenAiApiKey?.Value : Plugin.ApiKey?.Value);
            }
        }

        internal static bool HasKey => !string.IsNullOrEmpty(CurrentKey);

        internal static bool SetFromMenu(string key)
        {
            key = Normalize(key);
            if (string.IsNullOrEmpty(key)) return false;

            _sessionKey = key;
            var configKey = IsOpenAi ? Plugin.OpenAiApiKey : Plugin.ApiKey;
            if (Plugin.PersistApiKey != null && Plugin.PersistApiKey.Value && configKey != null)
            {
                configKey.Value = key;
                Plugin.Instance?.Config.Save();
            }
            return true;
        }

        internal static void ClearMenuKey()
        {
            _sessionKey = "";
            var configKey = IsOpenAi ? Plugin.OpenAiApiKey : Plugin.ApiKey;
            if (configKey != null)
            {
                configKey.Value = "";
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
