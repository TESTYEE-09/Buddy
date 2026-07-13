using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace LethalAICrewmate
{
    [BepInPlugin(ModGuid, ModName, ModVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string ModGuid = "com.lethalaicrewmate.buddy";
        public const string ModName = "LethalAICrewmate";
        public const string ModVersion = "1.0.0";

        internal static Plugin Instance;
        internal static ManualLogSource Log;

        internal static ConfigEntry<string> ApiKey;
        internal static ConfigEntry<string> Model;
        internal static ConfigEntry<string> CrewmateName;
        internal static ConfigEntry<string> Personality;
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> ChatHearRange;
        internal static ConfigEntry<float> ChatTriggerRange;
        internal static ConfigEntry<float> ObservationIntervalSeconds;

        private Harmony _harmony;
        internal static PluginHost Host;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            ApiKey = Config.Bind("OpenRouter", "ApiKey", "",
                "OpenRouter API key. Leave empty to keep the crewmate as a silent-but-functional NPC.");
            Model = Config.Bind("OpenRouter", "Model", "openai/gpt-oss-20b:free",
                "OpenRouter model id (free models work).");
            CrewmateName = Config.Bind("Crewmate", "Name", "Buddy",
                "Display name and chat command prefix for the AI crewmate.");
            Personality = Config.Bind("Crewmate", "Personality",
                "You are a helpful, slightly nervous Lethal Company crewmate. Stay in character.",
                "System-prompt personality fragment for LLM replies.");
            Enabled = Config.Bind("Crewmate", "Enabled", true,
                "Master toggle for spawning the AI crewmate.");
            ChatHearRange = Config.Bind("Crewmate", "ChatHearRange", 25f,
                "Max distance to hear crewmate chat (0 = everyone hears).");
            ChatTriggerRange = Config.Bind("Crewmate", "ChatTriggerRange", 25f,
                "Distance within which questions (ending with ?) trigger an LLM reply.");
            ObservationIntervalSeconds = Config.Bind("Crewmate", "ObservationIntervalSeconds", 0f,
                "Seconds between unsolicited LLM observations (0 = off). Randomizes ~45-90s style when set high.");

            try
            {
                var hostGo = new GameObject("LethalAICrewmateHost");
                DontDestroyOnLoad(hostGo);
                hostGo.hideFlags = HideFlags.HideAndDontSave;
                Host = hostGo.AddComponent<PluginHost>();

                _harmony = new Harmony(ModGuid);
                _harmony.PatchAll(typeof(Plugin).Assembly);
                Log.LogInfo($"{ModName} v{ModVersion} loaded.");
            }
            catch (Exception ex)
            {
                Log.LogError($"Failed to initialize {ModName}: {ex}");
            }
        }
    }

    /// <summary>
    /// Persistent MonoBehaviour used for coroutines (LLM requests) and late Update ticks.
    /// </summary>
    public class PluginHost : MonoBehaviour
    {
        private void Update()
        {
            try
            {
                CrewmateAI.HostUpdate();
                LlmClient.Tick();
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"PluginHost.Update: {ex}");
            }
        }
    }
}
