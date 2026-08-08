using System;
using System.Collections.Generic;
using UnityEngine;

namespace LethalAICrewmate
{
    /// <summary>
    /// Plays Buddy speech consistently on every peer and converts the host's generated clip into
    /// compact 16 kHz mono PCM for multiplayer replication. The Groq key/audio request stays host-only.
    /// </summary>
    public static class BuddyNetworkAudio
    {
        private const int NetworkSampleRate = 16000;
        private const int MaxNetworkSeconds = 15;
        private const float RealtimeVoiceGain = 1.12f;

        private static GameObject _audioGo;
        private static AudioSource _source;
        private static readonly Queue<QueuedClip> PlaybackQueue = new Queue<QueuedClip>();

        private struct QueuedClip
        {
            public AudioClip Clip;
            public Vector3 Position;
        }

        internal static bool IsPlaying => _source != null && _source.isPlaying;

        internal static void StopPlayback()
        {
            if (_source != null) _source.Stop();
            while (PlaybackQueue.Count > 0)
            {
                AudioClip clip = PlaybackQueue.Dequeue().Clip;
                if (clip != null) UnityEngine.Object.Destroy(clip);
            }
        }

        public static void Tick()
        {
            try
            {
                if (_audioGo != null && _source != null && _source.isPlaying)
                    _audioGo.transform.position = ResolveBuddyPosition(_audioGo.transform.position);
                if ((_source == null || !_source.isPlaying) && PlaybackQueue.Count > 0)
                {
                    QueuedClip next = PlaybackQueue.Dequeue();
                    PlayClip(next.Clip, next.Position);
                }
            }
            catch { }
        }

        public static void PlayHostClipAndReplicate(AudioClip clip, Vector3 worldPos)
        {
            if (clip == null) return;

            StopPlayback();
            BuddyAudioTuning.NormalizeHostClip(clip);
            PlayClip(clip, worldPos);

            try
            {
                if (!CrewmateSpawner.IsHost()) return;
                byte[] pcm = BuildNetworkPcm16(clip);
                if (pcm != null && pcm.Length > 0)
                    NetMessenger.BroadcastTtsPcm(pcm, NetworkSampleRate, ResolveBuddyPosition(worldPos));
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"Buddy TTS network encode: {ex.Message}");
            }
        }

        public static void PlayReplicatedPcm(byte[] pcm16, int sampleRate, Vector3 worldPos)
        {
            try
            {
                if (pcm16 == null || pcm16.Length < 2 || (pcm16.Length & 1) != 0) return;
                if (sampleRate < 8000 || sampleRate > 48000) return;

                int sampleCount = pcm16.Length / 2;
                float[] samples = new float[sampleCount];
                for (int i = 0; i < sampleCount; i++)
                {
                    int o = i * 2;
                    short s = (short)(pcm16[o] | (pcm16[o + 1] << 8));
                    samples[i] = s / 32768f;
                }

                var clip = AudioClip.Create("BuddyNetworkVoice", sampleCount, 1, sampleRate, false);
                clip.SetData(samples, 0);
                Plugin.Log?.LogInfo($"Buddy client PCM decoded length={clip.length:F2}s samples={sampleCount} rate={sampleRate}.");
                PlaybackQueue.Enqueue(new QueuedClip { Clip = clip, Position = worldPos });
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
            int sampleCount = pcm16.Length / 2;
            float[] samples = new float[sampleCount];
            for (int i = 0; i < sampleCount; i++)
            {
                float sample = BitConverter.ToInt16(pcm16, i * 2) / 32768f;
                // Native Realtime output bypasses the normal TTS clip normalizer. Add a small
                // transparent boost with a soft ceiling so Ash is easier to hear, not harsher.
                samples[i] = Mathf.Clamp(sample * RealtimeVoiceGain, -0.98f, 0.98f);
            }
            AudioClip clip = AudioClip.Create("BuddyRealtimeChunk", sampleCount, 1, sampleRate, false);
            clip.SetData(samples, 0);
            PlaybackQueue.Enqueue(new QueuedClip { Clip = clip, Position = worldPos });

            byte[] network = BuildNetworkPcm16(clip);
            if (network != null && network.Length > 0)
                NetMessenger.BroadcastTtsPcm(network, NetworkSampleRate, ResolveBuddyPosition(worldPos));
        }

        private static byte[] BuildNetworkPcm16(AudioClip clip)
        {
            if (clip == null || clip.samples <= 0 || clip.channels <= 0 || clip.frequency <= 0)
                return null;

            int channels = Mathf.Max(1, clip.channels);
            int sourceFrames = clip.samples;
            float[] source = new float[sourceFrames * channels];
            if (!clip.GetData(source, 0))
                return null;

            double ratio = (double)clip.frequency / NetworkSampleRate;
            int outputFrames = Math.Min(
                (int)Math.Ceiling(sourceFrames / ratio),
                NetworkSampleRate * MaxNetworkSeconds);
            if (outputFrames <= 0)
                return null;

            byte[] pcm = new byte[outputFrames * 2];
            for (int i = 0; i < outputFrames; i++)
            {
                int frame = Math.Min(sourceFrames - 1, (int)(i * ratio));
                int baseIndex = frame * channels;
                float mono = 0f;
                for (int ch = 0; ch < channels; ch++)
                    mono += source[baseIndex + ch];
                mono /= channels;
                mono = Mathf.Clamp(mono, -1f, 1f);

                short sample = (short)Mathf.RoundToInt(mono * 32767f);
                int o = i * 2;
                pcm[o] = (byte)(sample & 0xff);
                pcm[o + 1] = (byte)((sample >> 8) & 0xff);
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

        private static void PlayClip(AudioClip clip, Vector3 worldPos)
        {
            EnsureAudioSource();
            worldPos = ResolveBuddyPosition(worldPos);
            _audioGo.transform.position = worldPos;

            _source.Stop();
            if (_source.clip != null && _source.clip != clip)
            {
                var old = _source.clip;
                _source.clip = null;
                UnityEngine.Object.Destroy(old);
            }

            _source.clip = clip;
            _source.volume = Mathf.Clamp01(Plugin.TtsVolume?.Value ?? 0.85f);
            _source.mute = false;
            _source.loop = false;
            _source.playOnAwake = false;
            _source.dopplerLevel = 0f;
            _source.priority = 32;
            _source.bypassEffects = false;
            _source.bypassListenerEffects = false;
            _source.bypassReverbZones = false;

            float range = Plugin.ChatHearRange?.Value ?? 25f;
            if (range <= 0f)
            {
                _source.spatialBlend = 0f;
            }
            else
            {
                _source.spatialBlend = 1f;
                _source.spatialize = false;
                _source.rolloffMode = AudioRolloffMode.Linear;
                _source.minDistance = 3f;
                _source.maxDistance = Mathf.Max(6f, range);
            }

            BuddyAudioTuning.ConfigureSource(_source);
            _source.Play();
            Plugin.Log?.LogInfo($"Buddy audio playback started peer={(CrewmateSpawner.IsHost() ? "host" : "client")} length={clip.length:F2}s volume={_source.volume:F2} range={range:F0}m playing={_source.isPlaying}.");
        }

        private static void EnsureAudioSource()
        {
            if (_audioGo != null && _source != null) return;
            _audioGo = new GameObject("LethalAICrewmate_NetworkVoice");
            UnityEngine.Object.DontDestroyOnLoad(_audioGo);
            _source = _audioGo.AddComponent<AudioSource>();
        }
    }

}
