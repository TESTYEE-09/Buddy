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
    [BepInDependency("com.willis.lc.lethalsettings", BepInDependency.DependencyFlags.HardDependency)]
    public class Plugin : BaseUnityPlugin
    {
        public const string ModGuid = "com.lethalaicrewmate.buddy";
        public const string ModName = "Buddy";
        public const string ModVersion = "3.8.0";

        internal static Plugin Instance;
        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> TtsEnabled;
        internal static ConfigEntry<float> TtsVolume;
        internal static ConfigEntry<string> CrewmateName;
        internal static ConfigEntry<string> Personality;
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> ChatHearRange;
        internal static ConfigEntry<float> ChatTriggerRange;
        internal static ConfigEntry<float> ObservationIntervalSeconds;
        internal static ConfigEntry<bool> SlowBurnHorror;
        internal static ConfigEntry<bool> ResetSlowBurnProgress;
        internal static ConfigEntry<bool> DynamicPacing;
        internal static ConfigEntry<bool> FinalStageHostileSpawns;
        internal static ConfigEntry<bool> PlayerRelationships;
        internal static ConfigEntry<bool> EnvironmentAwareness;
        internal static ConfigEntry<bool> SocialAwareness;
        internal static ConfigEntry<bool> KeepGameVoiceDuringPtt;
        internal static ConfigEntry<bool> VoiceEnabled;
        internal static ConfigEntry<bool> AllowRemoteVoice;
        internal static ConfigEntry<KeyCode> VoiceKey;
        internal static ConfigEntry<KeyCode> VoiceAlternateKey;
        internal static ConfigEntry<float> VoiceMaxSeconds;
        internal static ConfigEntry<string> VoiceInputDevice;
        internal static ConfigEntry<string> RealtimeVoiceName;
        internal static ConfigEntry<string> ReasoningEffort;
        internal static ConfigEntry<bool> SaveResponses;
        internal static ConfigEntry<bool> SavePromptContext;
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
                RemoveObsolete("Vision", "Enabled", false);
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

            string legacyOpenAiKey = ReadLegacyConfigValue("OpenAI", "ApiKey")?.Trim() ?? "";
            if (!string.IsNullOrEmpty(legacyOpenAiKey))
            {
                bool persisted = OpenAiSecrets.ImportLegacyKey(legacyOpenAiKey);
                ClearLegacyPlaintextKey(true);
                if (!persisted)
                    Log.LogWarning("Legacy OpenAI key was removed from plaintext config and is available only for this session because Windows Credential Manager storage failed.");
            }
            TtsEnabled = Config.Bind("Voice", "SpokenReplies", true,
                "Let Buddy speak replies and replicate the host-generated audio to compatible clients.");
            TtsVolume = Config.Bind("Voice", "Volume", 1.25f,
                "Buddy voice volume 0–2. Speech is normalized once with a soft limiter before playback and replication.");

            CrewmateName = Config.Bind("Crewmate", "Name", "Buddy",
                "Display name and chat command prefix for the AI crewmate.");
            Personality = Config.Bind("Crewmate", "Personality",
                BuddyConversationPrompt.DefaultPersonality,
                "Optional personality flavor for Buddy. Core conversation/relevance rules always remain active.");
            Enabled = Config.Bind("Crewmate", "Enabled", true,
                "Master toggle for spawning the AI crewmate.");
            ChatHearRange = Config.Bind("Crewmate", "ChatHearRange", 0f,
                "Max distance to hear/see Buddy's voice captions and audio. 0 makes Buddy audible to everyone.");
            ChatTriggerRange = Config.Bind("Crewmate", "ChatTriggerRange", 60f,
                "Distance within which nearby push-to-talk can trigger Buddy. Typed chat never triggers Buddy.");
            ObservationIntervalSeconds = Config.Bind("Crewmate", "ObservationIntervalSeconds", 0f,
                "Periodic unsolicited observations are off by default; confirmed danger and important event callouts remain separate.");
            SlowBurnHorror = Config.Bind("Character", "SlowBurnHorror", true,
                "Let Buddy slowly become more unsettling across quota cycles, survived rounds and deaths he locally witnessed. Presentation only: never enables hostility, sabotage or invented sensor events.");
            ResetSlowBurnProgress = Config.Bind("Character", "ResetSlowBurnProgress", false,
                "Set true to reset the current save's slow-burn story to the ordinary coworker on the next host load. Automatically returns to false.");
            DynamicPacing = Config.Bind("Character", "DynamicPacing", true,
                "Let the horror director coordinate silence, spacing, staged watching beats and how much Buddy talks, based on the arc stage and live tension. Presentation only.");
            FinalStageHostileSpawns = Config.Bind("Character", "FinalStageHostileSpawns", true,
                "At the final story stage only, allow Buddy to occasionally release one of the current moon's own creatures near a working crewmate. Host-only, hard capped per round, and never triggerable by chat, a command or any remote player.");
            PlayerRelationships = Config.Bind("Character", "PlayerRelationships", true,
                "Let Buddy treat individual crewmates differently based on what he has actually seen them do. Stores at most eight sets of three small numbers per save: no names, IDs, chat or transcripts are written to disk.");
            EnvironmentAwareness = Config.Bind("Crewmate", "EnvironmentAwareness", true,
                "Report confirmed exits, closed or locked doors, placed hazards, weather and unusual entity situations to Buddy, with long cooldowns so he does not narrate the moon.");
            SocialAwareness = Config.Bind("Crewmate", "SocialAwareness", true,
                "Track who is speaking so Buddy waits his turn, answers the person who actually addressed him, and stays near whoever currently needs him.");

            VoiceEnabled = Config.Bind("Voice", "Enabled", true,
                "Push-to-talk for every modded player. Clients relay bounded mic audio to the host; only the host calls OpenAI Realtime.");
            RealtimeVoiceName = Config.Bind("Voice", "RealtimeVoiceName", BuddyAiArchitecture.DefaultRealtimeVoice,
                "Buddy's OpenAI Realtime voice. Valid values: " + string.Join(", ", BuddyAiArchitecture.RealtimeVoices) +
                ". The change applies from the next spoken reply.");
            AllowRemoteVoice = Config.Bind("Security", "AllowRemoteVoice", true,
                "Allow matching modded players to upload bounded push-to-talk audio to the host for the Realtime turn.");
            VoiceKey = Config.Bind("Voice", "PushToTalkKey", KeyCode.B,
                "Hold this key to record mic audio for Buddy. B avoids the game's common V push-to-talk binding; on clients the clip is relayed to the host.");
            VoiceAlternateKey = Config.Bind("Voice", "AlternatePushToTalkKey", KeyCode.None,
                "Optional second Buddy push-to-talk key. None disables it; do not use the game's normal voice-chat key unless you intend to send that audio to OpenAI Realtime.");
            VoiceMaxSeconds = Config.Bind("Voice", "MaxRecordSeconds", 8f,
                "Max push-to-talk length in seconds (capped at 12 by runtime).");
            KeepGameVoiceDuringPtt = Config.Bind("Voice", "KeepGameVoiceDuringPushToTalk", true,
                "Keep normal Lethal Company voice chat working while you talk to Buddy, so the rest of the crew still hear each other. Leave this on unless it conflicts with another voice mod.");
            VoiceInputDevice = Config.Bind("Voice", "InputDevice", "",
                "Optional microphone name (or part of its name). Empty uses the Windows default. Set this if Buddy records the wrong device.");
            ReasoningEffort = Config.Bind("AI", "ReasoningEffort", BuddyAiArchitecture.DefaultReasoningEffort,
                "How hard Buddy thinks before answering. Valid values: " + string.Join(", ", BuddyAiArchitecture.ReasoningEfforts) +
                ". Lower is faster and cheaper; higher gives better judgement on tool requests but a longer pause before he speaks. " +
                "Host-only, and it applies from the next spoken reply.");
            SaveResponses = Config.Bind("Logging", "SaveResponses", false,
                "Opt-in host-only journal of Buddy voice turns, replies, observations and tool results at BepInEx/LethalAICrewmate-responses.log. Voice audio is not separately transcribed into the journal. Enable only with the crew's informed consent.");
            SavePromptContext = Config.Bind("Logging", "SavePromptContext", false,
                "When response journaling is explicitly enabled, also record the system prompt and live sensor context. This may contain sensitive game and player data.");
            ConfigRevision = Config.Bind("Internal", "ConfigRevision", 0,
                "Internal migration marker. Do not edit.");

            try
            {
                var hostGo = new GameObject("LethalAICrewmateHost");
                DontDestroyOnLoad(hostGo);
                hostGo.hideFlags = HideFlags.HideAndDontSave;
                Host = hostGo.AddComponent<PluginHost>();

                _harmony = new Harmony(ModGuid);
                _harmony.PatchAll(typeof(Plugin).Assembly);

                // Collapse the historical multi-model OpenAI settings into the current two-path
                // architecture. Gameplay/security settings and deliberate personality text stay
                // untouched; only retired model defaults and privacy-safe migrations are reset.
                try
                {
                    if (ConfigRevision.Value < 12)
                    {
                        const string oldGoofyDefault = "Goofy male coworker: quick, useful, casually confident, mildly chaotic, and naturally funny without forcing a joke into every line.";
                        if (string.Equals(Personality.Value?.Trim(), oldGoofyDefault, StringComparison.Ordinal) ||
                            string.Equals(Personality.Value?.Trim(), BuddyConversationPrompt.PreviousDefaultPersonality, StringComparison.Ordinal))
                            Personality.Value = BuddyConversationPrompt.DefaultPersonality;
                        if (ConfigRevision.Value < 11) SaveResponses.Value = false;
                        ConfigRevision.Value = 12;
                        Log.LogInfo("Migrated Buddy AI settings to OpenAI Realtime.");
                    }
                    if (ConfigRevision.Value < 13)
                    {
                        // Raw chat, transcripts, prompts and sensor context are sensitive player
                        // data. Upgrades must not silently opt a lobby into collecting them.
                        SaveResponses.Value = false;
                        SavePromptContext.Value = false;
                        ConfigRevision.Value = 13;
                        Log.LogInfo("Disabled legacy response and prompt-context journaling; both settings are now opt-in.");
                    }
                    if (ConfigRevision.Value < 14)
                    {
                        ConfigRevision.Value = 14;
                        Log.LogInfo("Migrated Buddy to the single gpt-realtime-2.1-mini tool-calling architecture.");
                    }
                    if (ConfigRevision.Value < 15)
                    {
                        ConfigRevision.Value = 15;
                        Log.LogInfo("Removed the unused [Vision] settings; Buddy has no screenshot path.");
                    }
                    // v1.4.7/v1.5.3 revision-1/2 migrations were removed: the revision chain
                    // above already advances past them on every load, so they were unreachable.
                    Config.Save();
                }
                catch (Exception ex)
                {
                    Log.LogWarning($"Config self-heal: {ex.Message}");
                }

                BuddyAudioTuning.MigrateLegacyConfig();
                ConfigSafety.NormalizeOnce();
                BuddySettingsMenu.Register();
                RemoveObsoleteConfigEntries(removeLegacyGroqKey: true, removeLegacyOpenAiKey: true);
                Config.Save();

                if (!SaveResponses.Value)
                    ResponseJournal.DeleteExistingJournal();

                Log.LogInfo($"{ModName} v{ModVersion} loaded (model={BuddyAiArchitecture.OpenAiRealtimeModel}, native audio + tool calling).");
                if (SaveResponses.Value)
                    Log.LogWarning("Response journaling is enabled and stores Buddy voice-turn results, observations and tool results on this host: " + ResponseJournal.JournalPath);
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
                BuddyPacingDirector.Tick();
                BuddyRelationships.Tick();
                BuddyEnvironmentSensors.Tick();
                BuddyMalice.Tick();
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
