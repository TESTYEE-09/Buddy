using System;
using UnityEngine;

namespace LethalAICrewmate
{
    /// <summary>
    /// BepInEx config files are user-editable. Normalize values once after startup so malformed or
    /// extreme settings do not leak into UI, network payloads, audio, or update loops.
    /// </summary>
    internal static class ConfigSafety
    {
        private static bool _done;

        internal static void NormalizeOnce()
        {
            if (_done || Plugin.Instance == null) return;
            _done = true;

            try
            {
                bool changed = false;

                string name = CleanSingleLine(Plugin.CrewmateName?.Value, 24, "Buddy");
                changed |= SetIfDifferent(Plugin.CrewmateName, name);

                string provider = BuddyAiArchitecture.NormalizeProvider(
                    CleanSingleLine(Plugin.Provider?.Value, 16, BuddyAiArchitecture.OpenAiProvider));
                changed |= SetIfDifferent(Plugin.Provider, provider);

                string voice = CleanSingleLine(Plugin.TtsVoice?.Value, 16, "austin").ToLowerInvariant();
                string[] allowedVoices = { "autumn", "diana", "hannah", "austin", "daniel", "troy" };
                bool validVoice = false;
                foreach (string allowed in allowedVoices)
                    if (voice == allowed) { validVoice = true; break; }
                if (!validVoice) voice = "austin";
                changed |= SetIfDifferent(Plugin.TtsVoice, voice);

                // Empty is intentionally valid: it disables Orpheus direction tags.
                string direction = CleanSingleLineAllowEmpty(Plugin.TtsDirection?.Value, 48);
                changed |= SetIfDifferent(Plugin.TtsDirection, direction);

                changed |= ClampFloat(Plugin.TtsVolume, 0f, 1f, 1f);
                changed |= ClampFloat(Plugin.ChatHearRange, 0f, 120f, 0f);
                changed |= ClampFloat(Plugin.ChatTriggerRange, 0f, 120f, 60f);
                changed |= ClampFloat(Plugin.VoiceMaxSeconds, 1f, 12f, 8f);

                if (Plugin.ObservationIntervalSeconds != null)
                {
                    float v = Plugin.ObservationIntervalSeconds.Value;
                    float normalized = v <= 0f ? 0f : Mathf.Clamp(v, 10f, 600f);
                    if (!Mathf.Approximately(v, normalized))
                    {
                        Plugin.ObservationIntervalSeconds.Value = normalized;
                        changed = true;
                    }
                }

                if (changed)
                {
                    Plugin.Instance.Config.Save();
                    Plugin.Log?.LogInfo("Normalized LethalAICrewmate config values.");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"Config normalization: {ex.Message}");
            }
        }

        private static string CleanSingleLine(string value, int maxLength, string fallback)
        {
            string result = (value ?? "").Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
            if (string.IsNullOrEmpty(result)) result = fallback;
            if (result.Length > maxLength) result = result.Substring(0, maxLength).TrimEnd();
            return result;
        }

        private static string CleanSingleLineAllowEmpty(string value, int maxLength)
        {
            string result = (value ?? "").Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
            if (result.Length > maxLength) result = result.Substring(0, maxLength).TrimEnd();
            return result;
        }

        private static bool SetIfDifferent(BepInEx.Configuration.ConfigEntry<string> entry, string value)
        {
            if (entry == null || entry.Value == value) return false;
            entry.Value = value;
            return true;
        }

        private static bool ClampFloat(BepInEx.Configuration.ConfigEntry<float> entry, float min, float max, float fallback)
        {
            if (entry == null) return false;
            float v = entry.Value;
            if (float.IsNaN(v) || float.IsInfinity(v)) v = fallback;
            float normalized = Mathf.Clamp(v, min, max);
            if (Mathf.Approximately(entry.Value, normalized)) return false;
            entry.Value = normalized;
            return true;
        }
    }
}
