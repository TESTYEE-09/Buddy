using System;
using UnityEngine;

namespace LethalAICrewmate
{
    internal static class BuddyAudioTuning
    {
        private const float HearRange = 70f;
        private const float TriggerRange = 60f;
        private const float TargetRms = 0.16f;

        internal static void NormalizeHostClip(AudioClip clip)
        {
            if (!CrewmateSpawner.IsHost() || clip == null || clip.samples <= 0 || clip.channels <= 0)
                return;
            try
            {
                float[] samples = new float[clip.samples * clip.channels];
                if (!clip.GetData(samples, 0)) return;
                double sumSquares = 0d;
                for (int i = 0; i < samples.Length; i++) sumSquares += samples[i] * samples[i];
                float rms = (float)Math.Sqrt(sumSquares / Math.Max(1, samples.Length));
                float gain = rms > 0.0001f ? Mathf.Clamp(TargetRms / rms, 0.75f, 2.4f) : 1f;
                const double drive = 1.15;
                double divisor = Math.Tanh(drive);
                for (int i = 0; i < samples.Length; i++)
                    samples[i] = (float)(Math.Tanh(samples[i] * gain * drive) / divisor * 0.92);
                clip.SetData(samples, 0);
                Plugin.Log?.LogInfo($"Buddy voice normalized rms={rms:F3} gain={gain:F2} with soft limiter.");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"Buddy voice normalization: {ex.Message}");
            }
        }

        internal static void ConfigureSource(AudioSource source)
        {
            if (source == null) return;
            source.volume = Mathf.Clamp01(Plugin.TtsVolume?.Value ?? 1f);
            source.priority = 8;
            float range = Plugin.ChatHearRange?.Value ?? HearRange;
            if (range <= 0f)
            {
                source.spatialBlend = 0f;
                return;
            }
            source.spatialBlend = 1f;
            source.spatialize = false;
            source.rolloffMode = AudioRolloffMode.Linear;
            source.maxDistance = Mathf.Max(8f, range);
            source.minDistance = Mathf.Clamp(range * 0.32f, 14f, 24f);
            if (source.minDistance >= source.maxDistance)
                source.minDistance = Mathf.Max(2f, source.maxDistance * 0.55f);
        }

        internal static void MigrateLegacyConfig()
        {
            try
            {
                bool changed = false;
                string voice = Plugin.TtsVoice?.Value?.Trim() ?? "";
                string direction = Plugin.TtsDirection?.Value?.Trim() ?? "";
                if (string.Equals(voice, "troy", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(direction))
                {
                    Plugin.TtsVoice.Value = "austin";
                    Plugin.TtsDirection.Value = "friendly";
                    changed = true;
                }
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
