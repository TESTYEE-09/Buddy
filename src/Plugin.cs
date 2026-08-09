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
        public const string ModName = "Buddy";
        public const string ModVersion = "2.5.0";

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
        internal static ConfigEntry<string> RealtimeVoiceModel;
        internal static ConfigEntry<string> CrewmateName;
        internal static ConfigEntry<string> Personality;
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> ChatHearRange;
        internal static ConfigEntry<float> ChatTriggerRange;
        internal static ConfigEntry<float> ObservationIntervalSeconds;
        internal static ConfigEntry<bool> SlowBurnHorror;
        internal static ConfigEntry<bool> ResetSlowBurnProgress;
        internal static ConfigEntry<bool> VoiceEnabled;
        internal static ConfigEntry<bool> AllowRemoteVoice;
        internal static ConfigEntry<KeyCode> VoiceKey;
        internal static ConfigEntry<KeyCode> VoiceAlternateKey;
        internal static ConfigEntry<float> VoiceMaxSeconds;
        internal static ConfigEntry<string> VoiceInputDevice;
        internal static ConfigEntry<bool> VisionEnabled;
        internal static ConfigEntry<string> VisionModel;
        internal static ConfigEntry<bool> SaveResponses;
        internal static ConfigEntry<bool> RemoteVoiceInPublicLobbies;
        internal static ConfigEntry<bool> RemoteGameActionsInPublicLobbies;
        internal static ConfigEntry<int> ConfigRevision;

        private Harmony _harmony;
        internal static PluginHost Host;

        internal static void SaveConfiguration()
        {
            try { Instance?.Config.Save(); }
            catch (Exception ex) { Log?.LogDebug("Config save: " + ex.Message); }
        }

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            Provider = Config.Bind("AI", "Provider", "OpenAI",
                "AI API provider: OpenAI or Groq. The host alone sends requests and holds the selected provider key.");
            ApiKey = Config.Bind("Groq", "ApiKey", "",
                "Legacy plaintext Groq API key fallback. Menu-saved keys use Windows Credential Manager.");
            OpenAiApiKey = Config.Bind("OpenAI", "ApiKey", "",
                "Legacy plaintext OpenAI API key fallback. Menu-saved keys use Windows Credential Manager.");
            PersistApiKey = Config.Bind("Security", "PersistApiKey", false,
                "Legacy setting retained for compatibility. Menu keys now persist securely in Windows Credential Manager instead of plaintext config.");
            Model = Config.Bind("Groq", "Model", "gpt-5.6-luna",
                "Selected provider's chat model. OpenAI stock: gpt-5.6-luna through the Responses API.");
            SttModel = Config.Bind("Groq", "SttModel", "gpt-live-transcribe",
                "Selected provider's speech-to-text model. OpenAI stock: gpt-live-transcribe.");
            TtsModel = Config.Bind("Groq", "TtsModel", "gpt-4o-mini-tts",
                "Selected provider's text-to-speech model. OpenAI stock: gpt-4o-mini-tts.");
            TtsVoice = Config.Bind("Groq", "TtsVoice", "ash",
                "Selected provider's fallback TTS voice. Native OpenAI Realtime voice also uses Ash.");
            TtsEnabled = Config.Bind("Groq", "TtsEnabled", true,
                "Generate Buddy speech on the host and replicate it to compatible multiplayer clients.");
            TtsDirection = Config.Bind("Groq", "TtsDirection", "",
                "Optional Orpheus vocal direction (no brackets). Stock Buddy uses friendly for a lighter conversational delivery; empty = fully natural.");
            TtsVolume = Config.Bind("Groq", "TtsVolume", 1f,
                "Buddy voice volume 0–1. Speech is normalized once with a soft limiter before playback and replication.");

            RealtimeVoiceModel = Config.Bind("OpenAI", "RealtimeVoiceModel", "gpt-realtime-2.1-mini",
                "Native speech-to-speech model for OpenAI push-to-talk. Uses Ash, far-field noise reduction and low reasoning; text chat remains on the selected AI model.");

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
                BuddyConversationPrompt.DefaultPersonality,
                "Optional personality flavor for Buddy. Core conversation/relevance rules always remain active.");
            Enabled = Config.Bind("Crewmate", "Enabled", true,
                "Master toggle for spawning the AI crewmate.");
            ChatHearRange = Config.Bind("Crewmate", "ChatHearRange", 0f,
                "Max distance to hear/see Buddy chat and voice. 0 makes replies global so every player receives them.");
            ChatTriggerRange = Config.Bind("Crewmate", "ChatTriggerRange", 60f,
                "Distance within which nearby unaddressed questions and multiplayer push-to-talk can trigger Buddy. Addressing Buddy by text name still works normally.");
            ObservationIntervalSeconds = Config.Bind("Crewmate", "ObservationIntervalSeconds", 0f,
                "Seconds between unsolicited LLM observations (0 = off).");
            SlowBurnHorror = Config.Bind("Character", "SlowBurnHorror", true,
                "Let Buddy slowly become more unsettling across quota cycles, survived rounds and confirmed crew deaths. Presentation only: never enables hostility, sabotage or invented sensor events.");
            ResetSlowBurnProgress = Config.Bind("Character", "ResetSlowBurnProgress", false,
                "Set true to reset the current save's slow-burn story to the ordinary coworker on the next host load. Automatically returns to false.");

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
            SaveResponses = Config.Bind("Logging", "SaveResponses", false,
                "Opt in to a host-only journal containing raw player chat, voice transcripts, Buddy replies and tool results at BepInEx/LethalAICrewmate-responses.log.");
            RemoteVoiceInPublicLobbies = Config.Bind("Security", "RemoteVoiceInPublicLobbies", false,
                "Allow remote push-to-talk when the Steam lobby is public or its visibility cannot be verified. Off by default; known friends/invite-only lobbies remain allowed.");
            RemoteGameActionsInPublicLobbies = Config.Bind("Security", "RemoteGameActionsInPublicLobbies", false,
                "Allow remote players to route, buy, spawn or change ship/facility state when the lobby is public or its visibility cannot be verified. Off by default.");
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
                    // v1.7.0 removes the experimental Realtime chat path. Move only that test
                    // model back to the fast cost-focused Luna stack; preserve custom choices.
                    if (ConfigRevision.Value < 5)
                    {
                        // v1.7.0 migrated stock OpenAI installs to the fast cost-focused stack.
                        // The model clobber must be provider-guarded: Groq users keep their
                        // Whisper/Orpheus selections instead of getting OpenAI models forced
                        // onto Groq endpoints.
                        if (string.Equals(Provider.Value?.Trim(), "OpenAI", StringComparison.OrdinalIgnoreCase))
                        {
                            if (string.IsNullOrWhiteSpace(Model.Value) ||
                                Model.Value.StartsWith("gpt-realtime", StringComparison.OrdinalIgnoreCase))
                                Model.Value = "gpt-5.6-luna";
                            SttModel.Value = "gpt-4o-mini-transcribe";
                            TtsModel.Value = "tts-1";
                            TtsVoice.Value = "alloy";
                            TtsDirection.Value = "";
                        }
                        ConfigRevision.Value = 5;
                    }
                    // v1.7.1 gives untouched OpenAI installs the lighter coworker voice and
                    // faster character. Preserve every explicitly customized voice/personality.
                    if (ConfigRevision.Value < 6)
                    {
                        if (string.Equals(Provider.Value?.Trim(), "OpenAI", StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(TtsVoice.Value?.Trim(), "alloy", StringComparison.OrdinalIgnoreCase))
                            TtsVoice.Value = "echo";
                        if (string.Equals(Personality.Value?.Trim(), BuddyConversationPrompt.PreviousDefaultPersonality, StringComparison.Ordinal))
                            Personality.Value = BuddyConversationPrompt.DefaultPersonality;
                        ConfigRevision.Value = 6;
                    }
                    // v2.1 moves untouched OpenAI installs to the requested low-latency speech
                    // models. Preserve non-stock custom model and voice selections.
                    if (ConfigRevision.Value < 7)
                    {
                        if (string.Equals(Provider.Value?.Trim(), "OpenAI", StringComparison.OrdinalIgnoreCase))
                        {
                            if (string.IsNullOrWhiteSpace(SttModel.Value) ||
                                string.Equals(SttModel.Value?.Trim(), "gpt-4o-mini-transcribe", StringComparison.OrdinalIgnoreCase))
                                SttModel.Value = "gpt-live-transcribe";
                            if (string.IsNullOrWhiteSpace(TtsModel.Value) ||
                                string.Equals(TtsModel.Value?.Trim(), "tts-1", StringComparison.OrdinalIgnoreCase))
                                TtsModel.Value = "gpt-4o-mini-tts";
                            if (string.IsNullOrWhiteSpace(TtsVoice.Value) ||
                                string.Equals(TtsVoice.Value?.Trim(), "alloy", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(TtsVoice.Value?.Trim(), "echo", StringComparison.OrdinalIgnoreCase))
                                TtsVoice.Value = "cedar";
                            TtsDirection.Value = "";
                        }
                        ConfigRevision.Value = 7;
                    }
                    if (ConfigRevision.Value < 8)
                    {
                        if (string.Equals(Provider.Value?.Trim(), "OpenAI", StringComparison.OrdinalIgnoreCase) &&
                            (string.Equals(TtsVoice.Value?.Trim(), "cedar", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(TtsVoice.Value?.Trim(), "echo", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(TtsVoice.Value?.Trim(), "alloy", StringComparison.OrdinalIgnoreCase)))
                            TtsVoice.Value = "ash";
                        ConfigRevision.Value = 8;
                    }
                    // v2.2.4 uses the requested GPT Live Transcribe model for both native
                    // Realtime input transcription and non-native OpenAI STT paths.
                    if (ConfigRevision.Value < 9)
                    {
                        if (string.Equals(Provider.Value?.Trim(), "OpenAI", StringComparison.OrdinalIgnoreCase) &&
                            (string.IsNullOrWhiteSpace(SttModel.Value) ||
                             string.Equals(SttModel.Value?.Trim(), "gpt-realtime-whisper", StringComparison.OrdinalIgnoreCase)))
                            SttModel.Value = "gpt-live-transcribe";
                        ConfigRevision.Value = 9;
                    }
                    // v2.3 makes the stock public-release personality the dry, practical
                    // coworker voice. Preserve deliberate custom personality text.
                    if (ConfigRevision.Value < 10)
                    {
                        const string oldGoofyDefault = "Goofy male coworker: quick, useful, casually confident, mildly chaotic, and naturally funny without forcing a joke into every line.";
                        if (string.Equals(Personality.Value?.Trim(), oldGoofyDefault, StringComparison.Ordinal) ||
                            string.Equals(Personality.Value?.Trim(), BuddyConversationPrompt.PreviousDefaultPersonality, StringComparison.Ordinal))
                            Personality.Value = BuddyConversationPrompt.DefaultPersonality;
                        ConfigRevision.Value = 10;
                    }
                    // Raw chat and transcript persistence is a privacy-sensitive opt-in.
                    if (ConfigRevision.Value < 11)
                    {
                        SaveResponses.Value = false;
                        ConfigRevision.Value = 11;
                    }
                    if (string.IsNullOrWhiteSpace(Model.Value) ||
                        Model.Value.IndexOf("whisper", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        Model.Value.IndexOf("orpheus", StringComparison.OrdinalIgnoreCase) >= 0)
                        Model.Value = GroqSecrets.IsOpenAi ? "gpt-5.6-luna" : "openai/gpt-oss-120b";
                    if (string.Equals(Model.Value?.Trim(), "llama-3.3-70b-versatile", StringComparison.OrdinalIgnoreCase))
                        Model.Value = "openai/gpt-oss-120b";
                    if (string.IsNullOrWhiteSpace(VisionModel.Value))
                        VisionModel.Value = "qwen/qwen3.6-27b";
                    // v1.4.7/v1.5.3 revision-1/2 migrations were removed: the revision chain
                    // above already advances past them on every load, so they were unreachable.
                    // Vision is default-off for stock installs (config bind default false) and
                    // is now honored if a host explicitly enables it — no unconditional reset.
                    if (string.IsNullOrWhiteSpace(SttModel.Value))
                        SttModel.Value = GroqSecrets.IsOpenAi ? "gpt-live-transcribe" : "whisper-large-v3-turbo";
                    if (string.IsNullOrWhiteSpace(TtsModel.Value))
                        TtsModel.Value = GroqSecrets.IsOpenAi ? "gpt-4o-mini-tts" : "canopylabs/orpheus-v1-english";
                    Config.Save();
                }
                catch (Exception ex)
                {
                    Log.LogWarning($"Config self-heal: {ex.Message}");
                }

            BuddyAudioTuning.MigrateLegacyConfig();
            ConfigSafety.NormalizeOnce();

                Log.LogInfo($"{ModName} v{ModVersion} loaded (provider={GroqSecrets.ProviderName}, chat={Model.Value}, stt={SttModel.Value}, tts={TtsModel.Value}).");
                if (SaveResponses.Value)
                    Log.LogWarning("Response journaling is enabled and stores raw chat and voice transcripts on this host: " + ResponseJournal.JournalPath);
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
                BuddyCharacterDirector.Tick();
                LlmClient.Tick();
                VoiceCommand.Tick();
                BuddyClientVoice.Tick();
                OpenAiRealtimeVoiceClient.Tick();
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
