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
        public const string ModVersion = "1.3.0";

        internal static Plugin Instance;
        internal static ManualLogSource Log;

        // Groq (chat + STT + Orpheus TTS). OpenRouter keys still accepted as a legacy alias.
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
                "Groq API key. The host can set it from the Lethal Company main menu or this config file.");
            Model = Config.Bind("Groq", "Model", "qwen/qwen3.6-27b",
                "Groq chat model. Default qwen/qwen3.6-27b. Alternatives: meta-llama/llama-4-scout-17b-16e-instruct, llama-3.1-8b-instant.");
            SttModel = Config.Bind("Groq", "SttModel", "whisper-large-v3-turbo",
                "Groq STT model ONLY (must be whisper-large-v3-turbo or whisper-large-v3). Do NOT put chat models here.");
            TtsModel = Config.Bind("Groq", "TtsModel", "canopylabs/orpheus-v1-english",
                "Groq TTS model ONLY (must be canopylabs/orpheus-v1-english). Never put chat models here.");
            TtsVoice = Config.Bind("Groq", "TtsVoice", "troy",
                "Orpheus voice: autumn, diana, hannah (F) / austin, daniel, troy (M).");
            TtsEnabled = Config.Bind("Groq", "TtsEnabled", true,
                "Speak Buddy replies with Orpheus TTS (host hears 3D audio near Buddy).");
            TtsDirection = Config.Bind("Groq", "TtsDirection", "nervous",
                "Optional Orpheus vocal direction (no brackets), e.g. nervous, cheerful, whisper. Empty = natural.");
            TtsVolume = Config.Bind("Groq", "TtsVolume", 0.85f,
                "Buddy voice volume 0–1.");

            // Migrate older OpenRouter section if present and Groq key empty.
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
                    // Keep the Groq default unless they had a non-OpenRouter model.
                }
            }
            catch { /* ignore migration issues */ }

            CrewmateName = Config.Bind("Crewmate", "Name", "Buddy",
                "Display name and chat command prefix for the AI crewmate.");
            Personality = Config.Bind("Crewmate", "Personality",
                "Jumpy LC employee. Short radio callouts. Only real game threats — never invent sci-fi ship damage.",
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
            VisionEnabled = Config.Bind("Vision", "Enabled", true,
                "Send a screenshot of the host view to Qwen vision with each chat so Buddy can see (uses more API). Sensors always run.");

            try
            {
                var hostGo = new GameObject("LethalAICrewmateHost");
                DontDestroyOnLoad(hostGo);
                hostGo.hideFlags = HideFlags.HideAndDontSave;
                Host = hostGo.AddComponent<PluginHost>();
                hostGo.AddComponent<GroqKeyMenu>();

                _harmony = new Harmony(ModGuid);
                _harmony.PatchAll(typeof(Plugin).Assembly);

                // Self-heal mis-copied config keys (Model substring used to overwrite Stt/Tts models).
                try
                {
                    if (Model.Value != null && Model.Value.IndexOf("whisper", StringComparison.OrdinalIgnoreCase) >= 0)
                        Model.Value = "qwen/qwen3.6-27b";
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
