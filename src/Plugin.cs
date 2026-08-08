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
        public const string ModVersion = "1.6.4";

        internal static Plugin Instance;
        internal static ManualLogSource Log;

        internal static ConfigEntry<string> ApiKey;
        internal static ConfigEntry<string> OpenAiApiKey;
        internal static ConfigEntry<string> Provider;
        internal static ConfigEntry<bool> PersistApiKey;
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
        internal static ConfigEntry<bool> AllowRemoteVoice;
        internal static ConfigEntry<KeyCode> VoiceKey;
        internal static ConfigEntry<KeyCode> VoiceAlternateKey;
        internal static ConfigEntry<float> VoiceMaxSeconds;
        internal static ConfigEntry<string> VoiceInputDevice;
        internal static ConfigEntry<bool> VisionEnabled;
        internal static ConfigEntry<string> VisionModel;
        internal static ConfigEntry<int> ConfigRevision;

        private Harmony _harmony;
        internal static PluginHost Host;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            Provider = Config.Bind("AI", "Provider", "OpenAI",
                "AI API provider: OpenAI or Groq. The host alone sends requests and holds the selected provider key.");
            ApiKey = Config.Bind("Groq", "ApiKey", "",
                "Legacy plaintext Groq API key fallback. Prefer the LETHAL_AI_GROQ_API_KEY environment variable or a session-only key entered in the main menu.");
            OpenAiApiKey = Config.Bind("OpenAI", "ApiKey", "",
                "Legacy plaintext OpenAI API key fallback. Prefer LETHAL_AI_OPENAI_API_KEY or a session-only main-menu key.");
            PersistApiKey = Config.Bind("Security", "PersistApiKey", false,
                "Write a key entered in the menu to plaintext config. Disabled by default; prefer the selected provider's environment variable.");
            Model = Config.Bind("Groq", "Model", "gpt-realtime-2.1-mini",
                "Selected provider's chat model. OpenAI stock: gpt-realtime-2.1-mini (tested through Chat Completions; STT/TTS remain separate).");
            SttModel = Config.Bind("Groq", "SttModel", "gpt-4o-mini-transcribe",
                "Selected provider's speech-to-text model. OpenAI stock: gpt-4o-mini-transcribe.");
            TtsModel = Config.Bind("Groq", "TtsModel", "tts-1",
                "Selected provider's text-to-speech model. OpenAI stock: tts-1.");
            TtsVoice = Config.Bind("Groq", "TtsVoice", "alloy",
                "Selected provider's TTS voice. OpenAI stock: alloy; Groq Orpheus supports its named voices.");
            TtsEnabled = Config.Bind("Groq", "TtsEnabled", true,
                "Generate Buddy speech on the host and replicate it to compatible multiplayer clients.");
            TtsDirection = Config.Bind("Groq", "TtsDirection", "",
                "Optional Orpheus vocal direction (no brackets). Stock Buddy uses friendly for a lighter conversational delivery; empty = fully natural.");
            TtsVolume = Config.Bind("Groq", "TtsVolume", 1f,
                "Buddy voice volume 0–1. Speech is normalized once with a soft limiter before playback and replication.");

            // Very old private builds stored a provider key under [OpenRouter]. Only migrate a
            // Groq-shaped key; never silently send an OpenRouter key to the Groq endpoint.
            try
            {
                var legacyKey = Config.Bind("OpenRouter", "ApiKey", "", "Legacy setting; no longer used.");
                var legacyModel = Config.Bind("OpenRouter", "Model", "", "Legacy setting; no longer used.");
                string legacy = legacyKey.Value?.Trim() ?? "";
                if (PersistApiKey.Value && string.IsNullOrEmpty(ApiKey.Value) && legacy.StartsWith("gsk_", StringComparison.Ordinal))
                {
                    ApiKey.Value = legacy;
                    Log.LogInfo("Migrated a legacy Groq key into [Groq] ApiKey.");
                }
                else if (string.IsNullOrEmpty(ApiKey.Value) && !string.IsNullOrEmpty(legacy))
                {
                    Log.LogWarning("Legacy API key was not auto-migrated. Use LETHAL_AI_GROQ_API_KEY or enter it for this session from the main menu.");
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
            ChatHearRange = Config.Bind("Crewmate", "ChatHearRange", 0f,
                "Max distance to hear/see Buddy chat and voice. 0 makes replies global so every player receives them.");
            ChatTriggerRange = Config.Bind("Crewmate", "ChatTriggerRange", 60f,
                "Distance within which nearby unaddressed questions and multiplayer push-to-talk can trigger Buddy. Addressing Buddy by text name still works normally.");
            ObservationIntervalSeconds = Config.Bind("Crewmate", "ObservationIntervalSeconds", 0f,
                "Seconds between unsolicited LLM observations (0 = off).");

            VoiceEnabled = Config.Bind("Voice", "Enabled", true,
                "Push-to-talk for every modded player. Clients relay bounded mic audio to the host; only the host calls the selected transcription provider.");
            AllowRemoteVoice = Config.Bind("Security", "AllowRemoteVoice", true,
                "Allow matching remote players to upload bounded push-to-talk audio to the host for transcription. Disable this in public lobbies.");
            VoiceKey = Config.Bind("Voice", "PushToTalkKey", KeyCode.B,
                "Hold this key to record mic audio for Buddy. B avoids the game's common V push-to-talk binding; on clients the clip is relayed to the host.");
            VoiceAlternateKey = Config.Bind("Voice", "AlternatePushToTalkKey", KeyCode.V,
                "Optional second Buddy push-to-talk key. V also activates normal Lethal Company voice chat; set this equal to PushToTalkKey to disable the alternate.");
            VoiceMaxSeconds = Config.Bind("Voice", "MaxRecordSeconds", 8f,
                "Max push-to-talk length in seconds (capped at 12 by runtime).");
            VoiceInputDevice = Config.Bind("Voice", "InputDevice", "",
                "Optional microphone name (or part of its name). Empty uses the Windows default. Set this if Buddy records the wrong device.");
            VisionEnabled = Config.Bind("Vision", "Enabled", false,
                "Optional host screenshot analysis. Disabled in the stock text-only GPT-OSS setup.");
            VisionModel = Config.Bind("Vision", "Model", "qwen/qwen3.6-27b",
                "Groq multimodal model used for screenshot questions.");
            ConfigRevision = Config.Bind("Internal", "ConfigRevision", 0,
                "Internal migration marker. Do not edit.");

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
                    if (ConfigRevision.Value < 3)
                    {
                        Provider.Value = "OpenAI";
                        Model.Value = "gpt-5.6-luna";
                        SttModel.Value = "gpt-4o-mini-transcribe";
                        TtsModel.Value = "tts-1";
                        TtsVoice.Value = "alloy";
                        TtsDirection.Value = "";
                        ConfigRevision.Value = 3;
                    }
                    // v1.6.1 first tests the new realtime model as Buddy's chat brain while
                    // retaining the proven multiplayer STT and WAV TTS transport paths.
                    if (ConfigRevision.Value < 4)
                    {
                        if (string.Equals(Provider.Value?.Trim(), "OpenAI", StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(Model.Value?.Trim(), "gpt-5.6-luna", StringComparison.OrdinalIgnoreCase))
                            Model.Value = "gpt-realtime-2.1-mini";
                        ConfigRevision.Value = 4;
                    }
                    if (string.IsNullOrWhiteSpace(Model.Value) ||
                        Model.Value.IndexOf("whisper", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        Model.Value.IndexOf("orpheus", StringComparison.OrdinalIgnoreCase) >= 0)
                        Model.Value = GroqSecrets.IsOpenAi ? "gpt-realtime-2.1-mini" : "openai/gpt-oss-120b";
                    if (string.Equals(Model.Value?.Trim(), "llama-3.3-70b-versatile", StringComparison.OrdinalIgnoreCase))
                        Model.Value = "openai/gpt-oss-120b";
                    if (string.IsNullOrWhiteSpace(VisionModel.Value))
                        VisionModel.Value = "qwen/qwen3.6-27b";
                    // Apply 1.4.7 defaults once. After this marker is written, users can turn
                    // vision off or restore positional replies without those choices being reset.
                    if (ConfigRevision.Value < 1)
                    {
                        if (!VisionEnabled.Value)
                            VisionEnabled.Value = true;
                        if (Mathf.Approximately(ChatHearRange.Value, 70f))
                            ChatHearRange.Value = 0f;
                        ConfigRevision.Value = 1;
                    }
                    // v1.5.3 switches the stock experience to text-only GPT-OSS. Migrate only
                    // the prior stock Qwen model; preserve any other custom chat-model choice.
                    if (ConfigRevision.Value < 2)
                    {
                        if (string.Equals(Model.Value?.Trim(), "qwen/qwen3.6-27b", StringComparison.OrdinalIgnoreCase))
                            Model.Value = "openai/gpt-oss-120b";
                        VisionEnabled.Value = false;
                        ConfigRevision.Value = 2;
                    }
                    // Stock v1.5.3 is deliberately text-only: never capture the host screen,
                    // including when an older config previously opted into vision.
                    VisionEnabled.Value = false;
                    if (string.IsNullOrWhiteSpace(SttModel.Value))
                        SttModel.Value = GroqSecrets.IsOpenAi ? "gpt-4o-mini-transcribe" : "whisper-large-v3-turbo";
                    if (string.IsNullOrWhiteSpace(TtsModel.Value))
                        TtsModel.Value = GroqSecrets.IsOpenAi ? "tts-1" : "canopylabs/orpheus-v1-english";
                    Config.Save();
                }
                catch (Exception ex)
                {
                    Log.LogWarning($"Config self-heal: {ex.Message}");
                }

            BuddyAudioTuning.MigrateLegacyConfig();
            ConfigSafety.NormalizeOnce();

                Log.LogInfo($"{ModName} v{ModVersion} loaded (provider={GroqSecrets.ProviderName}, chat={Model.Value}, stt={SttModel.Value}, tts={TtsModel.Value}).");
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
                SpawnIntentSafety.Tick();
                LateJoinBinding.Tick();
                SessionCleanup.Tick();

                // Reliable spawn path: poll while landed (land events are easy to miss).
                if (Time.unscaledTime >= _nextSpawnPoll)
                {
                    _nextSpawnPoll = Time.unscaledTime + 1.25f;
                    CrewmateSpawner.PollSpawn();
                }

                CrewmateAI.HostUpdate();
                LlmClient.Tick();
                VoiceCommand.Tick();
                BuddyClientVoice.Tick();
                BuddyPoseSync.Tick();
                BuddyMovementWatchdog.Tick();
                BuddyDangerCallout.Tick();
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"PluginHost.Update: {ex}");
            }
        }

        private void LateUpdate()
        {
            try
            {
                // Apply remote authority after vanilla NetworkTransform/AI update work so the
                // client cannot be pulled back to a stale Masked position later in the frame.
                BuddyPoseSync.LateTick();
                BuddyNetworkAudio.Tick();
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"PluginHost.LateUpdate: {ex.Message}");
            }
        }
    }
}
