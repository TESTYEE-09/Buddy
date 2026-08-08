using System;
using System.Text;
using Dissonance;
using UnityEngine;

namespace LethalAICrewmate
{
    internal static class MicrophoneCapture
    {
        internal static string ResolveConfiguredDevice()
        {
            string requested = Plugin.VoiceInputDevice?.Value?.Trim() ?? "";
            string[] devices = Microphone.devices;
            if (devices == null || devices.Length == 0)
            {
                if (!string.IsNullOrEmpty(requested))
                    Plugin.Log?.LogWarning($"Configured Buddy microphone '{requested}' was not found; using Windows default.");
                return null;
            }

            if (!string.IsNullOrEmpty(requested))
            {
                string configured = FindMatchingDevice(devices, requested);
                if (!string.IsNullOrEmpty(configured)) return configured;
                Plugin.Log?.LogWarning($"Configured Buddy microphone '{requested}' was not found; using Lethal Company's active mic. Available: {string.Join(", ", devices)}");
            }

            // Dissonance owns the game's normal voice-chat microphone. Selecting the same active
            // device avoids silently recording a stale Windows default on remote clients.
            try
            {
                var comms = UnityEngine.Object.FindObjectOfType<DissonanceComms>();
                string active = comms?.MicrophoneCapture?.Device;
                if (string.IsNullOrWhiteSpace(active)) active = comms?.MicrophoneName;
                string matched = FindMatchingDevice(devices, active);
                if (!string.IsNullOrEmpty(matched)) return matched;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"Could not read Lethal Company voice device: {ex.Message}");
            }

            return null;
        }

        private static string FindMatchingDevice(string[] devices, string requested)
        {
            if (devices == null || string.IsNullOrWhiteSpace(requested)) return null;
            for (int i = 0; i < devices.Length; i++)
                if (string.Equals(devices[i], requested, StringComparison.OrdinalIgnoreCase))
                    return devices[i];
            for (int i = 0; i < devices.Length; i++)
                if (!string.IsNullOrEmpty(devices[i]) && devices[i].IndexOf(requested, StringComparison.OrdinalIgnoreCase) >= 0)
                    return devices[i];
            return null;
        }

        internal static byte[] EncodeAdaptiveMonoWav(AudioClip clip, int samplePos,
            out float inputRms, out float outputRms, out float appliedGain)
        {
            inputRms = 0f;
            outputRms = 0f;
            appliedGain = 1f;
            if (clip == null) return null;

            int channels = Mathf.Max(1, clip.channels);
            int samples = Mathf.Clamp(samplePos, 0, clip.samples);
            if (samples <= 0) samples = clip.samples;
            float[] interleaved = new float[samples * channels];
            if (!clip.GetData(interleaved, 0)) return null;

            float[] mono = new float[samples];
            double mean = 0d;
            for (int i = 0; i < samples; i++)
            {
                float sum = 0f;
                for (int channel = 0; channel < channels; channel++)
                    sum += interleaved[i * channels + channel];
                mono[i] = sum / channels;
                mean += mono[i];
            }
            mean /= Math.Max(1, samples);

            double inputSquares = 0d;
            float peak = 0f;
            for (int i = 0; i < samples; i++)
            {
                mono[i] -= (float)mean;
                peak = Mathf.Max(peak, Mathf.Abs(mono[i]));
                inputSquares += mono[i] * mono[i];
            }
            inputRms = (float)Math.Sqrt(inputSquares / Math.Max(1, samples));

            appliedGain = VoiceSignalMath.CalculateGain(inputRms, peak);

            short[] pcm = new short[samples];
            double outputSquares = 0d;
            const double drive = 1.1d;
            double limiterScale = Math.Tanh(drive);
            for (int i = 0; i < samples; i++)
            {
                float limited = (float)(Math.Tanh(mono[i] * appliedGain * drive) / limiterScale * 0.94d);
                limited = Mathf.Clamp(limited, -1f, 1f);
                pcm[i] = (short)Mathf.RoundToInt(limited * short.MaxValue);
                outputSquares += limited * limited;
            }
            outputRms = (float)Math.Sqrt(outputSquares / Math.Max(1, samples));

            int rate = clip.frequency > 0 ? clip.frequency : 16000;
            int dataLength = pcm.Length * 2;
            byte[] wav = new byte[44 + dataLength];
            WriteAscii(wav, 0, "RIFF");
            WriteInt32(wav, 4, 36 + dataLength);
            WriteAscii(wav, 8, "WAVE");
            WriteAscii(wav, 12, "fmt ");
            WriteInt32(wav, 16, 16);
            WriteInt16(wav, 20, 1);
            WriteInt16(wav, 22, 1);
            WriteInt32(wav, 24, rate);
            WriteInt32(wav, 28, rate * 2);
            WriteInt16(wav, 32, 2);
            WriteInt16(wav, 34, 16);
            WriteAscii(wav, 36, "data");
            WriteInt32(wav, 40, dataLength);
            for (int i = 0; i < pcm.Length; i++) WriteInt16(wav, 44 + i * 2, pcm[i]);
            return wav;
        }

        private static void WriteAscii(byte[] target, int offset, string value) => Encoding.ASCII.GetBytes(value).CopyTo(target, offset);
        private static void WriteInt32(byte[] target, int offset, int value) => BitConverter.GetBytes(value).CopyTo(target, offset);
        private static void WriteInt16(byte[] target, int offset, short value) => BitConverter.GetBytes(value).CopyTo(target, offset);
    }
}
