using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace LethalAICrewmate
{
    /// <summary>v1.4.3 loudness/range tuning layered on top of the v1.4.2 voice polish.</summary>
    internal static class BuddyPolish143
    {
        internal const float NewHearRange = 70f;
        internal const float NewTriggerRange = 60f;
        internal const float TargetRms = 0.16f;

        private static readonly FieldInfo NetworkAudioSourceField =
            AccessTools.Field(typeof(BuddyNetworkAudio), "_source");

        internal static void ApplyAdditionalHostGain(AudioClip clip)
        {
            try
            {
                if (!CrewmateSpawner.IsHost() || clip == null || clip.samples <= 0 || clip.channels <= 0)
                    return;

                float[] samples = new float[clip.samples * clip.channels];
                if (!clip.GetData(samples, 0)) return;
                double sumSquares = 0d;
                for (int i = 0; i < samples.Length; i++)
                    sumSquares += samples[i] * samples[i];
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
                Plugin.Log?.LogWarning($"Buddy v1.4.3 extra voice gain: {ex.Message}");
            }
        }

        internal static void TuneAudioBubble()
        {
            try
            {
                var source = NetworkAudioSourceField?.GetValue(null) as AudioSource;
                if (source == null) return;

                source.volume = Mathf.Clamp01(Plugin.TtsVolume?.Value ?? 1f);
                source.priority = 8;

                float range = Plugin.ChatHearRange?.Value ?? NewHearRange;
                if (range <= 0f)
                {
                    source.spatialBlend = 0f;
                    return;
                }

                source.spatialBlend = 1f;
                source.spatialize = false;
                source.rolloffMode = AudioRolloffMode.Linear;
                source.maxDistance = Mathf.Max(8f, range);

                // v1.4.2 started fading after ~10 m. Hold useful conversational volume much
                // farther before the linear falloff begins, while still preserving direction.
                float minDistance = Mathf.Clamp(range * 0.32f, 14f, 24f);
                if (minDistance >= source.maxDistance)
                    minDistance = Mathf.Max(2f, source.maxDistance * 0.55f);
                source.minDistance = minDistance;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"Buddy v1.4.3 audio bubble: {ex.Message}");
            }
        }
    }

    /// <summary>Migrate untouched v1.4.2 stock distance values without overwriting custom ranges.</summary>
    [HarmonyPatch(typeof(PluginHost), "Update")]
    internal static class Patch_PluginHost_BuddyPolish143Config
    {
        private static bool _done;

        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static void Prefix()
        {
            if (_done || Plugin.Instance == null) return;
            _done = true;

            try
            {
                bool changed = false;
                if (Plugin.ChatHearRange != null && Mathf.Approximately(Plugin.ChatHearRange.Value, 50f))
                {
                    Plugin.ChatHearRange.Value = BuddyPolish143.NewHearRange;
                    changed = true;
                }
                if (Plugin.ChatTriggerRange != null && Mathf.Approximately(Plugin.ChatTriggerRange.Value, 45f))
                {
                    Plugin.ChatTriggerRange.Value = BuddyPolish143.NewTriggerRange;
                    changed = true;
                }

                if (changed)
                {
                    Plugin.Instance.Config.Save();
                    Plugin.Log?.LogInfo("Applied v1.4.3 Buddy distance tuning (70m hearing, 60m conversation/PTT)." );
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"Buddy v1.4.3 config migration: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Run after the v1.4.2 host gain and source tuning. The extra gain is applied only on the
    /// host before network PCM creation, so clients receive one already-limited boosted signal.
    /// </summary>
    [HarmonyPatch(typeof(BuddyNetworkAudio), "PlayClip")]
    internal static class Patch_BuddyNetworkAudio_Polish143
    {
        [HarmonyPrefix, HarmonyPriority(Priority.Last)]
        private static void Prefix(AudioClip clip)
        {
            BuddyPolish143.ApplyAdditionalHostGain(clip);
        }

        [HarmonyPostfix, HarmonyPriority(Priority.Last)]
        private static void Postfix()
        {
            BuddyPolish143.TuneAudioBubble();
        }
    }
}
