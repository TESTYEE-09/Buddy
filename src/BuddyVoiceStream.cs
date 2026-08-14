using System;
using UnityEngine;

namespace LethalAICrewmate
{
    /// <summary>
    /// Gapless playback for Buddy's streamed Realtime speech.
    ///
    /// Realtime produces audio faster than real time, so the previous "queue a short AudioClip per
    /// delta and play them one at a time" model failed two ways at once: the bounded clip queue
    /// dropped the middle of a sentence as soon as generation outran playback, and every chunk
    /// carried its own leading silence plus a frame of AudioSource restart latency, which turned a
    /// continuous line into a stutter. Everything now feeds one continuous streaming AudioSource
    /// backed by a ring buffer, so speech plays exactly as generated.
    ///
    /// The ring is written from the main thread and drained from Unity's audio thread. Both sides
    /// take the same lock, but the expensive resample happens outside it so the audio thread is
    /// never blocked for more than a memory copy.
    /// </summary>
    internal static class BuddyVoiceStream
    {
        internal const int StreamRate = 24000;
        private const int RingSamples = StreamRate * 20;
        private const int ClipSamples = StreamRate;
        // Smallest lead-in Buddy ever starts on. Realtime delivers the opening of a line in a burst
        // and then settles to roughly real time, so a lead-in sized to the first chunk (0.12s) ran
        // the ring dry partway through every single line: captured sessions re-buffered on the first
        // reply, again on the second, and only played cleanly once the cushion had healed to ~0.5s.
        // Starting there costs half a second of latency once and removes the mid-sentence cut that
        // players hear as Buddy being talked over or clipped.
        private const float MinCushionSeconds = 0.55f;
        // The audio thread drains in real time while chunks arrive in bursts. When a burst is late
        // the ring runs dry and the line audibly cuts, so every starvation permanently widens the
        // cushion for this session: a good connection stays fast, a jittery one heals to gapless.
        private const float CushionGrowthSeconds = 0.25f;
        private const float MaxCushionSeconds = 1.5f;
        // A stream that stopped arriving is finished: play whatever is left even if it is short.
        private const float StreamIdleSeconds = 0.25f;
        // OnAudioRead removes samples from the ring before Unity's audio pipeline has actually
        // presented that callback. Stopping in the same frame that _count reaches zero clips the
        // last callback (usually the final syllable). Keep the source alive for a short drain tail
        // measured from the first empty observation, not from the last network write.
        private const float PlaybackDrainTailSeconds = 0.25f;

        private static readonly object Gate = new object();
        private static readonly float[] Ring = new float[RingSamples];
        private static int _read;
        private static int _count;

        private static GameObject _go;
        private static AudioSource _source;
        private static AudioClip _clip;
        private static bool _playing;
        private static float _playStartedAt = -999f;
        private static float _lastWriteAt = -999f;
        private static float _emptySince = -1f;
        // Set on Unity's audio thread when the ring could not fill a callback, promoted to a real
        // underrun by Write only if more of the same line then arrives. Draining the ring at the end
        // of a finished line is not a fault and must not widen the cushion.
        private static bool _ranDry;
        private static bool _starved;
        private static float _cushionSeconds = MinCushionSeconds;

        internal static bool HasAudio
        {
            get { lock (Gate) return _count > 0; }
        }

        /// <summary>Appends mono PCM16 to the playback stream, resampling to the stream rate.</summary>
        internal static void Write(byte[] pcm16, int sampleRate, float gain)
        {
            if (pcm16 == null || pcm16.Length < 2 || (pcm16.Length & 1) != 0) return;
            if (sampleRate < 8000 || sampleRate > 48000) return;

            int sourceSamples = pcm16.Length / 2;
            int targetSamples = sampleRate == StreamRate
                ? sourceSamples
                : (int)Math.Ceiling(sourceSamples * (double)StreamRate / sampleRate);
            if (targetSamples <= 0) return;

            float[] converted = new float[targetSamples];
            double step = (double)sampleRate / StreamRate;
            for (int i = 0; i < targetSamples; i++)
            {
                double at = sampleRate == StreamRate ? i : i * step;
                int a = Math.Min(sourceSamples - 1, (int)at);
                int b = Math.Min(sourceSamples - 1, a + 1);
                float sa = BitConverter.ToInt16(pcm16, a * 2) / 32768f;
                float sb = BitConverter.ToInt16(pcm16, b * 2) / 32768f;
                converted[i] = Mathf.Clamp(Mathf.Lerp(sa, sb, (float)(at - a)) * gain, -0.98f, 0.98f);
            }

            lock (Gate)
            {
                if (_ranDry)
                {
                    // Audio for this line kept coming after playback had nothing left to play, so
                    // the gap the listener just heard was a buffering fault, not the end of a line.
                    _ranDry = false;
                    _starved = true;
                }
                for (int i = 0; i < converted.Length; i++)
                {
                    if (_count == RingSamples)
                    {
                        // Twenty seconds is far beyond any single Buddy line. Dropping the oldest
                        // sample keeps a runaway stream from wedging playback entirely.
                        _read = _read + 1 == RingSamples ? 0 : _read + 1;
                        _count--;
                    }
                    int write = _read + _count;
                    if (write >= RingSamples) write -= RingSamples;
                    Ring[write] = converted[i];
                    _count++;
                }
            }
            _lastWriteAt = Time.unscaledTime;
            _emptySince = -1f;
        }

        /// <summary>Drops everything buffered and silences Buddy immediately.</summary>
        internal static void Clear()
        {
            lock (Gate)
            {
                _read = 0;
                _count = 0;
                _ranDry = false;
                _starved = false;
                // _cushionSeconds is deliberately kept: it is what this PC and connection have
                // proven they need, and relearning it would cut the first line of every reply.
            }
            if (_source != null)
            {
                try { _source.Stop(); } catch { }
            }
            _playing = false;
            _playStartedAt = -999f;
            _lastWriteAt = -999f;
            _emptySince = -1f;
        }

        internal static void Tick(Vector3 position)
        {
            try
            {
                EnsureSource();
                if (_go != null) _go.transform.position = position;

                int buffered;
                bool starved;
                float cushion;
                lock (Gate)
                {
                    buffered = _count;
                    starved = _starved;
                    _starved = false;
                    if (starved && _cushionSeconds < MaxCushionSeconds)
                        _cushionSeconds = Mathf.Min(MaxCushionSeconds, _cushionSeconds + CushionGrowthSeconds);
                    cushion = _cushionSeconds;
                }

                bool streamFinished = Time.unscaledTime - _lastWriteAt > StreamIdleSeconds;

                if (_playing && starved && !streamFinished)
                {
                    // The line is still being generated but the ring ran dry, so the audio thread is
                    // padding Buddy's sentence with silence. Hold output until the wider cushion is
                    // refilled: one short pause reads as a breath, a stream of micro-gaps reads as
                    // a broken mod.
                    _source.Stop();
                    _playing = false;
                    // Info, not Debug: this is the exact event a player reports as "his replies get
                    // cut off", and it has to be visible in an ordinary log without debug enabled.
                    Plugin.Log?.LogInfo($"Buddy voice stream re-buffering; cushion now {cushion:F2}s.");
                    return;
                }

                if (!_playing)
                {
                    if (buffered >= (int)(StreamRate * cushion) || (buffered > 0 && streamFinished))
                    {
                        ApplyOutputSettings();
                        _source.Play();
                        _playing = true;
                        _playStartedAt = Time.unscaledTime;
                        Plugin.Log?.LogInfo(
                            $"Buddy voice stream started peer={(CrewmateSpawner.IsHost() ? "host" : "client")} buffered={buffered / (float)StreamRate:F2}s cushion={cushion:F2}s.");
                    }
                    return;
                }

                if (buffered > 0)
                {
                    _emptySince = -1f;
                }
                else if (_emptySince < 0f)
                {
                    _emptySince = Time.unscaledTime;
                }
                else if (Time.unscaledTime - _emptySince >= PlaybackDrainTailSeconds)
                {
                    _source.Stop();
                    _playing = false;
                    _playStartedAt = -999f;
                    _emptySince = -1f;
                    // The final audio callback of a finished line always drains the ring, so it set
                    // _ranDry without any fault having occurred. Leaving it set makes the first
                    // Write of the *next* response report a starvation that never happened, which
                    // widens the cushion every tool turn until it pins at the maximum and Buddy
                    // stalls for a second and a half before every reply.
                    lock (Gate) _ranDry = false;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning("Buddy voice stream tick: " + ex.Message);
            }
        }

        private static void EnsureSource()
        {
            if (_go != null && _source != null && _clip != null) return;
            if (_go == null)
            {
                _go = new GameObject("LethalAICrewmate_VoiceStream");
                UnityEngine.Object.DontDestroyOnLoad(_go);
            }
            if (_source == null) _source = _go.AddComponent<AudioSource>();
            if (_clip == null)
                _clip = AudioClip.Create("BuddyVoiceStream", ClipSamples, 1, StreamRate, true, OnAudioRead, OnSetPosition);
            _source.clip = _clip;
            _source.loop = true;
            _source.playOnAwake = false;
        }

        private static void ApplyOutputSettings()
        {
            _source.mute = false;
            _source.dopplerLevel = 0f;
            _source.priority = 32;

            float range = Plugin.ChatHearRange?.Value ?? 70f;
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
        }

        /// <summary>Runs on Unity's audio thread; must not allocate or block.</summary>
        private static void OnAudioRead(float[] data)
        {
            if (data == null) return;
            lock (Gate)
            {
                int available = Math.Min(data.Length, _count);
                if (available < data.Length) _ranDry = true;
                for (int i = 0; i < available; i++)
                {
                    data[i] = Ring[_read];
                    _read = _read + 1 == RingSamples ? 0 : _read + 1;
                }
                _count -= available;
                for (int i = available; i < data.Length; i++) data[i] = 0f;
            }
        }

        // The ring buffer owns playback position, so clip seeks carry no state.
        private static void OnSetPosition(int position) { }
    }
}
