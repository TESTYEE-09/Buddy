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
        public const string ModVersion = "1.4.3";

        internal static Plugin Instance;
        internal static ManualLogSource Log;

        internal static ConfigEntry<string> ApiKey;
        internal static ConfigEntry<string> Model;
        internal static ConfigEntry<string> SttModel;
        internal static ConfigEntry<string> TtsModel;
        internal static ConfigEntry<string> TtsVoice;
        internal static ConfigEntry<bool> TtsEnabled;
        internal static ConfigEntry<string> TtsDirection;
        internal static ConfigEntry<float> TtsVolume;
        internal static ConfigEntry<string> CrewmateName;
        internal static ConfigEntry<string> Personality;
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> ChatHearRange;
        internal static ConfigEntry<float> ChatTriggerRange;
        internal static ConfigEntry<float> ObservationIntervalSeconds;
        internal static ConfigEntry<bool> VoiceEnabled;
        internal static ConfigEntry<KeyCode> VoiceKey;
        internal static ConfigEntry<float> VoiceMaxSeconds;
        internal static ConfigEntry<bool> VisionEnabled;

        private Harmony _harmony;
        internal static PluginHost Host;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            ApiKey = Config.Bind("Groq", "ApiKey", "",
                "Groq API key. The host can set/test it from the Lethal Company main menu or this config file. Never shared with multiplayer clients.");
            Model = Config.Bind("Groq", "Model", "llama-3.3-70b-versatile",
                "Groq chat model. Production default: llama-3.3-70b-versatile. For optional vision use qwen/qwen3.6-27b (preview).");
            SttModel = Config.Bind("Groq", "SttModel", "whisper-large-v3-turbo",
                "Groq speech-to-text model. Recommended: whisper-large-v3-turbo.");
            TtsModel = Config.Bind("Groq", "TtsModel", "canopylabs/orpheus-v1-english",
                "Groq text-to-speech model. Orpheus is optional; chat keeps working if TTS is unavailable.");
            TtsVoice = Config.Bind("Groq", "TtsVoice", "austin",
                "Orpheus voice: autumn, diana, hannah (F) / austin, daniel, troy (M). Austin is the brighter stock Buddy voice.");
            TtsEnabled = Config.Bind("Groq", "TtsEnabled", true,
                "Generate Buddy speech on the host and replicate it to compatible multiplayer clients.");
            TtsDirection = Config.Bind("Groq", "TtsDirection", "friendly",
                "Optional Orpheus vocal direction (no brackets). Stock Buddy uses friendly for a lighter conversational delivery; empty = fully natural.");
            TtsVolume = Config.Bind("Groq", "TtsVolume", 1f,
                "Buddy voice volume 0–1. v1.4.3 applies bounded host-side gain before playback/network replication for stronger speech.");

            // Very old private builds stored a provider key under [OpenRouter]. Only migrate a
            // Groq-shaped key; never silently send an OpenRouter key to the Groq endpoint.
            try
            {
                var legacyKey = Config.Bind("OpenRouter", "ApiKey", "", "Legacy setting; no longer used.");
                var legacyModel = Config.Bind("OpenRouter", "Model", "", "Legacy setting; no longer used.");
                string legacy = legacyKey.Value?.Trim() ?? "";
                if (string.IsNullOrEmpty(ApiKey.Value) && legacy.StartsWith("gsk_", StringComparison.Ordinal))
                {
                    ApiKey.Value = legacy;
                    Log.LogInfo("Migrated a legacy Groq key into [Groq] ApiKey.");
                }
                else if (string.IsNullOrEmpty(ApiKey.Value) && !string.IsNullOrEmpty(legacy))
                {
                    Log.LogWarning("Ignored legacy non-Groq API key. Add a Groq key from the main menu.");
                }

                // Keep legacyModel bound only so old configs remain readable; model migration is
                // intentionally not automatic because provider model IDs are not interchangeable.
                _ = legacyModel.Value;
            }
            catch { /* migration must never block plugin startup */ }

            CrewmateName = Config.Bind("Crewmate", "Name", "Buddy",
                "Display name and chat command prefix for the AI crewmate.");
            Personality = Config.Bind("Crewmate", "Personality",
                "Friendly, useful crewmate with dry low-key humor. Calm most of the time, a little nervous only when something is actually dangerous.",
                "Optional personality flavor for Buddy. Core conversation/relevance rules always remain active.");
            Enabled = Config.Bind("Crewmate", "Enabled", true,
                "Master toggle for spawning the AI crewmate.");
            ChatHearRange = Config.Bind("Crewmate", "ChatHearRange", 70f,
                "Max distance to hear/see Buddy chat and positional voice (0 = everyone hears). Stock v1.4.3 range is 70m.");
            ChatTriggerRange = Config.Bind("Crewmate", "ChatTriggerRange", 60f,
                "Distance within which nearby unaddressed questions and multiplayer push-to-talk can trigger Buddy. Addressing Buddy by text name still works normally.");
            ObservationIntervalSeconds = Config.Bind("Crewmate", "ObservationIntervalSeconds", 0f,
                "Seconds between unsolicited LLM observations (0 = off).");

            VoiceEnabled = Config.Bind("Voice", "Enabled", true,
                "Push-to-talk for every modded player. Clients relay bounded mic audio to the host; only the host uses the Groq Whisper API key.");
            VoiceKey = Config.Bind("Voice", "PushToTalkKey", KeyCode.V,
                "Hold this key to record mic audio for Buddy. On clients the clip is relayed to the host for transcription.");
            VoiceMaxSeconds = Config.Bind("Voice", "MaxRecordSeconds", 8f,
                "Max push-to-talk length in seconds (capped at 12 by runtime).");
            VisionEnabled = Config.Bind("Vision", "Enabled", false,
                "Attach a host-view screenshot to chat. Requires a vision-capable Groq model such as qwen/qwen3.6-27b. Off by default for production reliability/cost.");

            try
            {
                var hostGo = new GameObject("LethalAICrewmateHost");
                DontDestroyOnLoad(hostGo);
                hostGo.hideFlags = HideFlags.HideAndDontSave;
                Host = hostGo.AddComponent<PluginHost>();
                hostGo.AddComponent<GroqKeyMenu>();

                _harmony = new Harmony(ModGuid);
                _harmony.PatchAll(typeof(Plugin).Assembly);

                // Self-heal obviously crossed audio/chat model config values from older builds.
                try
                {
                    if (string.IsNullOrWhiteSpace(Model.Value) ||
                        Model.Value.IndexOf("whisper", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        Model.Value.IndexOf("orpheus", StringComparison.OrdinalIgnoreCase) >= 0)
                        Model.Value = "llama-3.3-70b-versatile";
                    if (SttModel.Value == null || SttModel.Value.IndexOf("whisper", StringComparison.OrdinalIgnoreCase) < 0)
                        SttModel.Value = "whisper-large-v3-turbo";
                    if (TtsModel.Value == null || TtsModel.Value.IndexOf("orpheus", StringComparison.OrdinalIgnoreCase) < 0)
                        TtsModel.Value = "canopylabs/orpheus-v1-english";
                    Config.Save();
                }
                catch (Exception ex)
                {
                    Log.LogWarning($"Config self-heal: {ex.Message}");
                }

                Log.LogInfo($"{ModName} v{ModVersion} loaded (chat={Model.Value}, stt={SttModel.Value}, tts={TtsModel.Value}).");
            }
            catch (Exception ex)
            {
                Log.LogError($"Failed to initialize {ModName}: {ex}");
            }
        }
    }

    /// <summary>
    /// Persistent MonoBehaviour used for networking, coroutines (LLM/STT), and update ticks.
    /// </summary>
    public class PluginHost : MonoBehaviour
    {
        private float _nextSpawnPoll;

        private void Update()
        {
            try
            {
                // Every peer needs its named-message handlers registered, not only the host.
                NetMessenger.Tick();

                // Reliable spawn path: poll while landed (land events are easy to miss).
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
