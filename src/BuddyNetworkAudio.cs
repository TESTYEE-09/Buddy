using System;
using UnityEngine;

namespace LethalAICrewmate
{
    /// <summary>
    /// Plays Buddy speech consistently on every peer and converts the host's generated audio into
    /// compact 16 kHz mono PCM for multiplayer replication. The OpenAI key and request stay host-only.
    /// Both peers feed the same continuous <see cref="BuddyVoiceStream"/>, so streamed Realtime audio
    /// plays gaplessly instead of being chopped into competing clips.
    /// </summary>
    public static class BuddyNetworkAudio
    {
        private const int NetworkSampleRate = 16000;
        private const float RealtimeVoiceGain = 1.38f;

        private static Vector3 _lastPosition;

        internal static bool IsPlaying => BuddyVoiceStream.HasAudio;

        internal static void StopPlayback() => BuddyVoiceStream.Clear();

        public static void Tick()
        {
            _lastPosition = ResolveBuddyPosition(_lastPosition);
            BuddyVoiceStream.Tick(_lastPosition);
        }

        /// <summary>Client-side playback of speech the host already generated.</summary>
        public static void PlayReplicatedPcm(byte[] pcm16, int sampleRate, Vector3 worldPos)
        {
            try
            {
                // The host bakes its gain into the replicated stream, so clients play it flat.
                _lastPosition = worldPos;
                BuddyVoiceStream.Write(pcm16, sampleRate, 1f);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"Buddy replicated TTS playback: {ex.Message}");
            }
        }

        internal static void QueueHostPcm16(byte[] pcm16, int sampleRate, Vector3 worldPos)
        {
            if (!CrewmateSpawner.IsHost() || pcm16 == null || pcm16.Length < 2 || (pcm16.Length & 1) != 0) return;
            if (sampleRate < 8000 || sampleRate > 48000) return;

            // Native Realtime output bypasses the old clip normalizer, so apply a small transparent
            // boost with a soft ceiling here and replicate the same audio the host hears.
            float loudness = Mathf.Max(1f, Mathf.Clamp(Plugin.TtsVolume?.Value ?? 1.25f, 0f, 2f));
            float gain = RealtimeVoiceGain * loudness;

            _lastPosition = worldPos;
            BuddyVoiceStream.Write(pcm16, sampleRate, gain);

            byte[] network = BuildNetworkPcm16(pcm16, sampleRate, gain);
            if (network != null && network.Length > 0)
                NetMessenger.BroadcastTtsPcm(network, NetworkSampleRate, ResolveBuddyPosition(worldPos));
        }

        private static byte[] BuildNetworkPcm16(byte[] pcm16, int sampleRate, float gain)
        {
            int sourceSamples = pcm16.Length / 2;
            int outputSamples = sampleRate == NetworkSampleRate
                ? sourceSamples
                : (int)Math.Ceiling(sourceSamples * (double)NetworkSampleRate / sampleRate);
            if (outputSamples <= 0) return null;

            byte[] pcm = new byte[outputSamples * 2];
            double step = (double)sampleRate / NetworkSampleRate;
            for (int i = 0; i < outputSamples; i++)
            {
                int frame = Math.Min(sourceSamples - 1, (int)(i * step));
                float sample = BitConverter.ToInt16(pcm16, frame * 2) / 32768f;
                short value = (short)Mathf.RoundToInt(Mathf.Clamp(sample * gain, -0.98f, 0.98f) * 32767f);
                int o = i * 2;
                pcm[o] = (byte)(value & 0xff);
                pcm[o + 1] = (byte)((value >> 8) & 0xff);
            }
            return pcm;
        }

        private static Vector3 ResolveBuddyPosition(Vector3 fallback)
        {
            try
            {
                var primary = CrewmateRegistry.GetPrimary();
                if (primary?.Enemy != null)
                    return primary.Enemy.transform.position + Vector3.up * 1.7f;
            }
            catch { /* use fallback */ }
            return fallback;
        }
    }
}
