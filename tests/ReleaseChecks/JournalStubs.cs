// Test-only stubs so ResponseJournal can be exercised without Unity or the real BepInEx plugin host.
namespace LethalAICrewmate
{
    public static class Plugin
    {
        public const string ModVersion = "test";
        public static BepInEx.Configuration.ConfigEntry<bool> SaveResponses;
        public static BepInEx.Configuration.ConfigEntry<bool> SavePromptContext;
        public static BepInEx.Configuration.ConfigEntry<string> CrewmateName;
        public static BepInEx.Logging.ManualLogSource Log;
    }

    public static class OpenAiSecrets
    {
        public static string ProviderName => "test";
    }
}

namespace BepInEx
{
    public static class Paths
    {
        public static string BepInExRootPath { get; set; }
    }
}

namespace BepInEx.Logging
{
    public sealed class ManualLogSource
    {
        public void LogDebug(string message) { }
    }
}

namespace BepInEx.Configuration
{
    public sealed class ConfigEntry<T>
    {
        public T Value { get; set; }
    }
}
