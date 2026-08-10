using System;
using UnityEngine;

namespace LethalAICrewmate
{
    internal static class BuddyAudioTuning
    {
        private const float HearRange = 70f;
        private const float TriggerRange = 60f;
        internal static void ConfigureSource(AudioSource source)
        {
            if (source == null) return;
            source.volume = Mathf.Clamp01(Plugin.TtsVolume?.Value ?? 1.25f);
            // Preserve the generated voice exactly. Latency is handled by faster API models and
            // processing tiers rather than making Buddy talk unnaturally fast.
            source.pitch = 1f;
            source.priority = 0;
            source.mute = false;
            source.enabled = true;
            source.ignoreListenerPause = true;
            source.outputAudioMixerGroup = null;
            source.bypassEffects = true;
            source.bypassListenerEffects = true;
            source.bypassReverbZones = true;

            // Spatial behaviour is owned by BuddyVoiceStream, which decides from the
            // ChatHearRange setting (0 = global listener-relative, >0 = positional).
            // Keeping that decision here would override it and make the range setting dead code.
        }

        internal static void MigrateLegacyConfig()
        {
            try
            {
                bool changed = false;
                if (Plugin.TtsVolume != null && Mathf.Approximately(Plugin.TtsVolume.Value, 0.85f))
                {
                    Plugin.TtsVolume.Value = 1f;
                    changed = true;
                }
                if (Plugin.ChatHearRange != null &&
                    (Mathf.Approximately(Plugin.ChatHearRange.Value, 25f) || Mathf.Approximately(Plugin.ChatHearRange.Value, 50f)))
                {
                    Plugin.ChatHearRange.Value = HearRange;
                    changed = true;
                }
                if (Plugin.ChatTriggerRange != null &&
                    (Mathf.Approximately(Plugin.ChatTriggerRange.Value, 25f) || Mathf.Approximately(Plugin.ChatTriggerRange.Value, 45f)))
                {
                    Plugin.ChatTriggerRange.Value = TriggerRange;
                    changed = true;
                }
                if (Plugin.VoiceKey != null && Plugin.VoiceKey.Value == KeyCode.V)
                {
                    Plugin.VoiceKey.Value = KeyCode.B;
                    changed = true;
                }
                if (Plugin.VoiceAlternateKey != null && Plugin.VoiceAlternateKey.Value == KeyCode.V)
                {
                    Plugin.VoiceAlternateKey.Value = KeyCode.None;
                    changed = true;
                }
                if (changed)
                {
                    Plugin.Instance.Config.Save();
                    Plugin.Log?.LogInfo("Migrated legacy Buddy voice/range defaults.");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"Buddy config migration: {ex.Message}");
            }
        }
    }
}
