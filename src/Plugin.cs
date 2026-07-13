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
        public const string ModVersion = "1.1.0";

        internal static Plugin Instance;
        internal static ManualLogSource Log;

        // Groq (chat + speech-to-text). OpenRouter keys still accepted as legacy alias.
        internal static ConfigEntry<string> ApiKey;
        internal static ConfigEntry<string> Model;
        internal static ConfigEntry<string> SttModel;
        internal static ConfigEntry<string> CrewmateName;
        internal static ConfigEntry<string> Personality;
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> ChatHearRange;
        internal static ConfigEntry<float> ChatTriggerRange;
        internal static ConfigEntry<float> ObservationIntervalSeconds;
        internal static ConfigEntry<bool> VoiceEnabled;
        internal static ConfigEntry<KeyCode> VoiceKey;
        internal static ConfigEntry<float> VoiceMaxSeconds;

        private Harmony _harmony;
        internal static PluginHost Host;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            ApiKey = Config.Bind("Groq", "ApiKey", "",
                "Groq API key from https://console.groq.com/keys. Leave empty for silent NPC (commands still work).");
            Model = Config.Bind("Groq", "Model", "llama-3.1-8b-instant",
                "Groq chat model id (fast default: llama-3.1-8b-instant). Also good: llama-3.3-70b-versatile, openai/gpt-oss-20b.");
            SttModel = Config.Bind("Groq", "SttModel", "whisper-large-v3-turbo",
                "Groq speech-to-text model (whisper-large-v3-turbo or whisper-large-v3).");

            // Migrate older OpenRouter section if present and Groq key empty
            try
            {
                var legacyKey = Config.Bind("OpenRouter", "ApiKey", "", "Legacy — use Groq.ApiKey instead.");
                var legacyModel = Config.Bind("OpenRouter", "Model", "", "Legacy — use Groq.Model instead.");
                if (string.IsNullOrEmpty(ApiKey.Value) && !string.IsNullOrEmpty(legacyKey.Value))
                {
                    ApiKey.Value = legacyKey.Value;
                    Log.LogInfo("Migrated API key from OpenRouter config section to Groq.");
                }
                if (!string.IsNullOrEmpty(legacyModel.Value) &&
                    (string.IsNullOrEmpty(Model.Value) || Model.Value == "llama-3.1-8b-instant") &&
                    legacyModel.Value.IndexOf("openrouter", StringComparison.OrdinalIgnoreCase) < 0 &&
                    !legacyModel.Value.EndsWith(":free", StringComparison.OrdinalIgnoreCase))
                {
                    // keep groq default unless they had a non-openrouter model
                }
            }
            catch { /* ignore migration issues */ }

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
                "Seconds between unsolicited LLM observations (0 = off).");

            VoiceEnabled = Config.Bind("Voice", "Enabled", true,
                "Hold VoiceKey to talk to Buddy via Groq Whisper speech-to-text (host only).");
            VoiceKey = Config.Bind("Voice", "PushToTalkKey", KeyCode.V,
                "Hold this key to record mic audio for Buddy. Release to transcribe + send.");
            VoiceMaxSeconds = Config.Bind("Voice", "MaxRecordSeconds", 8f,
                "Max push-to-talk length in seconds (capped at 15).");

            try
            {
                var hostGo = new GameObject("LethalAICrewmateHost");
                DontDestroyOnLoad(hostGo);
                hostGo.hideFlags = HideFlags.HideAndDontSave;
                Host = hostGo.AddComponent<PluginHost>();

                _harmony = new Harmony(ModGuid);
                _harmony.PatchAll(typeof(Plugin).Assembly);
                Log.LogInfo($"{ModName} v{ModVersion} loaded (Groq chat + STT).");
            }
            catch (Exception ex)
            {
                Log.LogError($"Failed to initialize {ModName}: {ex}");
            }
        }
    }

    /// <summary>
    /// Persistent MonoBehaviour used for coroutines (LLM/STT) and late Update ticks.
    /// </summary>
    public class PluginHost : MonoBehaviour
    {
        private float _nextSpawnPoll;

        private void Update()
        {
            try
            {
                // Reliable spawn path: poll while landed (land events are easy to miss)
                if (Time.unscaledTime >= _nextSpawnPoll)
                {
                    _nextSpawnPoll = Time.unscaledTime + 1.25f;
                    CrewmateSpawner.PollSpawn();
                }

                CrewmateAI.HostUpdate();
                LlmClient.Tick();
                VoiceCommand.Tick();
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"PluginHost.Update: {ex}");
            }
        }
    }
}
