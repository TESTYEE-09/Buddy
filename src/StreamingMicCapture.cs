using System;
using UnityEngine;

namespace LethalAICrewmate
{
    /// <summary>
    /// Converts live Unity microphone frames into bounded 16 kHz mono PCM16 chunks for Buddy's
    /// streaming Realtime path. Keeping one fixed wire rate makes host and remote-client voice use
    /// the exact same transport and lets the host be the only peer that ever talks to OpenAI.
    /// </summary>
    internal static class StreamingMicCapture
    {
        internal const int WireRate = 16000;
        internal const int ChunkMilliseconds = 100;

        internal static int RecommendedSourceFrames(AudioClip clip)
        {
            int rate = clip != null && clip.frequency > 0 ? clip.frequency : WireRate;
            return Math.Max(2, rate * ChunkMilliseconds / 1000);
        }

        internal static byte[] EncodeChunk(
            AudioClip clip,
            int startFrame,
            int frameCount,
            ref float smoothedGain,
            out float inputRms,
            out float outputRms)
        {
            inputRms = 0f;
            outputRms = 0f;
            if (clip == null || frameCount < 2 || startFrame < 0 || startFrame >= clip.samples)
                return null;

            frameCount = Math.Min(frameCount, clip.samples - startFrame);
            if (frameCount < 2) return null;

            int channels = Math.Max(1, clip.channels);
            var interleaved = new float[frameCount * channels];
            if (!clip.GetData(interleaved, startFrame)) return null;

            var mono = new float[frameCount];
            double mean = 0d;
            for (int i = 0; i < frameCount; i++)
            {
                float sum = 0f;
                for (int channel = 0; channel < channels; channel++)
                    sum += interleaved[i * channels + channel];
                mono[i] = sum / channels;
                mean += mono[i];
            }
            mean /= frameCount;

            double inputSquares = 0d;
            float peak = 0f;
            for (int i = 0; i < frameCount; i++)
            {
                mono[i] -= (float)mean;
                peak = Mathf.Max(peak, Mathf.Abs(mono[i]));
                inputSquares += mono[i] * mono[i];
            }
            inputRms = (float)Math.Sqrt(inputSquares / frameCount);

            float targetGain = VoiceSignalMath.CalculateGain(inputRms, peak);
            smoothedGain = smoothedGain <= 0f
                ? targetGain
                : Mathf.Lerp(smoothedGain, targetGain, 0.35f);

            const double drive = 1.1d;
            double limiterScale = Math.Tanh(drive);
            var processed = new float[frameCount];
            for (int i = 0; i < frameCount; i++)
            {
                float limited = (float)(Math.Tanh(mono[i] * smoothedGain * drive) / limiterScale * 0.94d);
                processed[i] = Mathf.Clamp(limited, -1f, 1f);
            }

            int sourceRate = clip.frequency > 0 ? clip.frequency : WireRate;
            int targetSamples = (int)Math.Floor(frameCount * (double)WireRate / sourceRate);
            // 16 kHz -> 24 kHz is exactly 3:2 in the host client. Keeping each transport chunk on
            // an even sample boundary prevents half-sample phase resets between chunks.
            targetSamples &= ~1;
            if (targetSamples < 2) return null;

            byte[] pcm = new byte[targetSamples * 2];
            double outputSquares = 0d;
            for (int i = 0; i < targetSamples; i++)
            {
                double sourcePos = i * (double)sourceRate / WireRate;
                int a = Math.Min(frameCount - 1, (int)sourcePos);
                int b = Math.Min(frameCount - 1, a + 1);
                float sample = Mathf.Lerp(processed[a], processed[b], (float)(sourcePos - a));
                outputSquares += sample * sample;
                short value = (short)Mathf.RoundToInt(Mathf.Clamp(sample, -1f, 1f) * short.MaxValue);
                int offset = i * 2;
                pcm[offset] = (byte)(value & 0xff);
                pcm[offset + 1] = (byte)((value >> 8) & 0xff);
            }
            outputRms = (float)Math.Sqrt(outputSquares / targetSamples);
            return pcm;
        }

        internal static float CalculatePcm16Rms(byte[] pcm)
        {
            if (pcm == null || pcm.Length < 2 || (pcm.Length & 1) != 0) return 0f;
            int samples = pcm.Length / 2;
            double squares = 0d;
            for (int i = 0; i < samples; i++)
            {
                float value = BitConverter.ToInt16(pcm, i * 2) / 32768f;
                squares += value * value;
            }
            return (float)Math.Sqrt(squares / samples);
        }
    }
}
