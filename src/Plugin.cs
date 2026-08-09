using System;
using System.IO;
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
        public const string ModVersion = "2.8.0";

        internal static Plugin Instance;
        internal static ManualLogSource Log;

        internal static ConfigEntry<string> Provider;
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
        internal static ConfigEntry<bool> SlowBurnHorror;
        internal static ConfigEntry<bool> ResetSlowBurnProgress;
        internal static ConfigEntry<bool> VoiceEnabled;
        internal static ConfigEntry<bool> AllowRemoteVoice;
        internal static ConfigEntry<KeyCode> VoiceKey;
        internal static ConfigEntry<KeyCode> VoiceAlternateKey;
        internal static ConfigEntry<float> VoiceMaxSeconds;
        internal static ConfigEntry<string> VoiceInputDevice;
        internal static ConfigEntry<bool> VisionEnabled;
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

        internal static void ClearLegacyPlaintextKey(bool openAi)
        {
            if (Instance == null) return;
            try
            {
                string section = openAi ? "OpenAI" : "Groq";
                var definition = new ConfigDefinition(section, "ApiKey");
                bool saveOnSet = Instance.Config.SaveOnConfigSet;
                Instance.Config.SaveOnConfigSet = false;
                try
                {
                    Instance.Config.Bind(definition, "", new ConfigDescription("Obsolete plaintext key."));
                    Instance.Config.Remove(definition);
                }
                finally { Instance.Config.SaveOnConfigSet = saveOnSet; }
                Instance.Config.Save();
            }
            catch (Exception ex) { Log?.LogDebug("Legacy key clear: " + ex.Message); }
        }

        private string ReadLegacyConfigValue(string section, string key)
        {
            try
            {
                string path = Config?.ConfigFilePath;
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
                string currentSection = "";
                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.StartsWith("[") && line.EndsWith("]"))
                    {
                        currentSection = line.Substring(1, line.Length - 2).Trim();
                        continue;
                    }
                    if (!string.Equals(currentSection, section, StringComparison.OrdinalIgnoreCase)) continue;
                    int equals = line.IndexOf('=');
                    if (equals <= 0 || !string.Equals(line.Substring(0, equals).Trim(), key, StringComparison.OrdinalIgnoreCase)) continue;
                    return line.Substring(equals + 1).Trim();
                }
            }
            catch (Exception ex) { Log?.LogDebug("Legacy config read: " + ex.Message); }
            return null;
        }

        private void RemoveObsoleteConfigEntries(bool removeLegacyGroqKey, bool removeLegacyOpenAiKey)
        {
            // Binding first consumes BepInEx's orphaned value; removing the active definition
            // then lets the next Save actually delete it from disk and Configuration Manager.
            bool saveOnSet = Config.SaveOnConfigSet;
            Config.SaveOnConfigSet = false;
            try
            {
                RemoveObsolete("OpenAI", "RealtimeVoiceModel", "");
                if (removeLegacyOpenAiKey) RemoveObsolete("OpenAI", "ApiKey", "");
                RemoveObsolete("Groq", "Model", "");
                RemoveObsolete("Groq", "SttModel", "");
                RemoveObsolete("Groq", "TtsModel", "");
                RemoveObsolete("Groq", "TtsEnabled", true);
                RemoveObsolete("Groq", "TtsVolume", 1f);
                if (removeLegacyGroqKey) RemoveObsolete("Groq", "ApiKey", "");
                RemoveObsolete("Vision", "Model", "");
                RemoveObsolete("Security", "PersistApiKey", false);
                RemoveObsolete("OpenRouter", "ApiKey", "");
                RemoveObsolete("OpenRouter", "Model", "");
            }
            finally { Config.SaveOnConfigSet = saveOnSet; }
        }

        private void RemoveObsolete<T>(string section, string key, T fallback)
        {
            try
            {
                var definition = new ConfigDefinition(section, key);
                Config.Bind(definition, fallback, new ConfigDescription("Obsolete Buddy setting; removed during migration."));
                Config.Remove(definition);
            }
            catch (Exception ex) { Log?.LogDebug("Obsolete config cleanup " + section + "." + key + ": " + ex.Message); }
        }

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            Provider = Config.Bind("AI", "Provider", BuddyAiArchitecture.OpenAiProvider,
                "Buddy experience: OpenAI (recommended realtime voice agent) or Groq (separate free/low-cost pipeline). The host alone holds the selected key.");
            string legacyGroqKey = ReadLegacyConfigValue("Groq", "ApiKey")?.Trim() ?? "";
            string legacyOpenAiKey = ReadLegacyConfigValue("OpenAI", "ApiKey")?.Trim() ?? "";
            bool removeLegacyGroqKey = string.IsNullOrEmpty(legacyGroqKey) || GroqSecrets.ImportLegacyKey(false, legacyGroqKey);
            bool removeLegacyOpenAiKey = string.IsNullOrEmpty(legacyOpenAiKey) || GroqSecrets.ImportLegacyKey(true, legacyOpenAiKey);
            TtsVoice = Config.Bind("Groq", "TtsVoice", "austin",
                "Groq Orpheus voice. OpenAI uses the native Ash voice inside its Realtime session.");
            bool legacySpokenReplies = !bool.TryParse(ReadLegacyConfigValue("Groq", "TtsEnabled"), out bool oldSpoken) || oldSpoken;
            float legacyVolume = float.TryParse(ReadLegacyConfigValue("Groq", "TtsVolume"), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float oldVolume) ? oldVolume : 1f;
            TtsEnabled = Config.Bind("Voice", "SpokenReplies", legacySpokenReplies,
                "Let Buddy speak replies and replicate the host-generated audio to compatible clients.");
            TtsDirection = Config.Bind("Groq", "TtsDirection", "friendly",
                "Optional Orpheus vocal direction (no brackets). Stock Buddy uses friendly for a lighter conversational delivery; empty = fully natural.");
            TtsVolume = Config.Bind("Voice", "Volume", legacyVolume,
                "Buddy voice volume 0–1. Speech is normalized once with a soft limiter before playback and replication.");

            // Very old private builds stored a provider key under [OpenRouter]. Read it without
            // binding legacy controls into the current config UI.
            try
            {
                string legacy = ReadLegacyConfigValue("OpenRouter", "ApiKey")?.Trim() ?? "";
                bool.TryParse(ReadLegacyConfigValue("Security", "PersistApiKey"), out bool legacyPersistenceEnabled);
                if (legacyPersistenceEnabled && string.IsNullOrEmpty(legacyGroqKey) && legacy.StartsWith("gsk_", StringComparison.Ordinal))
                {
                    if (GroqSecrets.ImportLegacyKey(false, legacy))
                        Log.LogInfo("Migrated a legacy Groq key into Windows Credential Manager.");
                    else
                        Log.LogWarning("Legacy Groq key is available for this session but could not be saved securely.");
                }
                else if (string.IsNullOrEmpty(legacyGroqKey) && !string.IsNullOrEmpty(legacy))
                {
                    Log.LogWarning("Legacy API key was not auto-migrated. Use LETHAL_AI_GROQ_API_KEY or enter it for this session from the main menu.");
                }
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
                "Let Buddy slowly become more unsettling across quota cycles, survived rounds and deaths he locally witnessed. Presentation only: never enables hostility, sabotage or invented sensor events.");
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
                "Optional host screenshot analysis for explicit visual questions. Disabled by default.");
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
                hostGo.AddComponent<BuddySetupMenu>();

                _harmony = new Harmony(ModGuid);
                _harmony.PatchAll(typeof(Plugin).Assembly);

                // Collapse the historical multi-model OpenAI settings into the current two-path
                // architecture. Gameplay/security settings and deliberate personality text stay
                // untouched; only provider model defaults and privacy-safe migrations are reset.
                try
                {
                    if (ConfigRevision.Value < 12)
                    {
                        const string oldGoofyDefault = "Goofy male coworker: quick, useful, casually confident, mildly chaotic, and naturally funny without forcing a joke into every line.";
                        if (string.Equals(Personality.Value?.Trim(), oldGoofyDefault, StringComparison.Ordinal) ||
                            string.Equals(Personality.Value?.Trim(), BuddyConversationPrompt.PreviousDefaultPersonality, StringComparison.Ordinal))
                            Personality.Value = BuddyConversationPrompt.DefaultPersonality;
                        if (ConfigRevision.Value < 11) SaveResponses.Value = false;
                        Provider.Value = BuddyAiArchitecture.NormalizeProvider(Provider.Value);
                        if (string.IsNullOrWhiteSpace(TtsVoice.Value) ||
                            string.Equals(TtsVoice.Value?.Trim(), "ash", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(TtsVoice.Value?.Trim(), "alloy", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(TtsVoice.Value?.Trim(), "cedar", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(TtsVoice.Value?.Trim(), "echo", StringComparison.OrdinalIgnoreCase))
                            TtsVoice.Value = "austin";
                        ConfigRevision.Value = 12;
                        Log.LogInfo("Migrated Buddy AI settings to OpenAI Realtime + separate Groq Qwen architecture.");
                    }
                    // v1.4.7/v1.5.3 revision-1/2 migrations were removed: the revision chain
                    // above already advances past them on every load, so they were unreachable.
                    // Vision is default-off for stock installs (config bind default false) and
                    // is now honored if a host explicitly enables it — no unconditional reset.
                    Config.Save();
                }
                catch (Exception ex)
                {
                    Log.LogWarning($"Config self-heal: {ex.Message}");
                }

                BuddyAudioTuning.MigrateLegacyConfig();
                ConfigSafety.NormalizeOnce();
                RemoveObsoleteConfigEntries(removeLegacyGroqKey, removeLegacyOpenAiKey);
                Config.Save();

                string architecture = GroqSecrets.IsOpenAi
                    ? BuddyAiArchitecture.OpenAiRealtimeModel + " + " + BuddyAiArchitecture.OpenAiTranscriptionModel
                    : BuddyAiArchitecture.GroqChatModel + " + " + BuddyAiArchitecture.GroqTranscriptionModel + " + " + BuddyAiArchitecture.GroqSpeechModel;
                Log.LogInfo($"{ModName} v{ModVersion} loaded (provider={GroqSecrets.ProviderName}, pipeline={architecture}).");
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
                BuddyAutonomy.Tick();
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
