using System;

namespace LethalAICrewmate
{
    internal static class TransportValidation
    {
        internal static bool IsExactChunk(int totalBytes, int chunkBytes, int offset, int length)
        {
            if (totalBytes <= 0 || chunkBytes <= 0 || offset < 0 || offset >= totalBytes)
                return false;
            if (offset % chunkBytes != 0)
                return false;
            return length == Math.Min(chunkBytes, totalBytes - offset);
        }

        internal static bool TryValidateMonoPcm16Wav(
            byte[] wav,
            int maxBytes,
            float minSeconds,
            float maxSeconds,
            float minRms,
            out string reason)
        {
            reason = "";
            if (wav == null || wav.Length < 44 || wav.Length > maxBytes)
            {
                reason = "invalid byte length";
                return false;
            }
            if (wav[0] != 'R' || wav[1] != 'I' || wav[2] != 'F' || wav[3] != 'F' ||
                wav[8] != 'W' || wav[9] != 'A' || wav[10] != 'V' || wav[11] != 'E')
            {
                reason = "invalid WAV header";
                return false;
            }

            int channels = wav[22] | (wav[23] << 8);
            int sampleRate = BitConverter.ToInt32(wav, 24);
            int bits = wav[34] | (wav[35] << 8);
            int dataBytes = BitConverter.ToInt32(wav, 40);
            if (channels != 1 || bits != 16 || sampleRate < 8000 || sampleRate > 48000 ||
                dataBytes <= 0 || dataBytes != wav.Length - 44 || (dataBytes & 1) != 0)
            {
                reason = "unsupported or inconsistent WAV format";
                return false;
            }

            float duration = dataBytes / (sampleRate * 2f);
            if (duration < minSeconds || duration > maxSeconds)
            {
                reason = $"duration {duration:F2}s outside limits";
                return false;
            }

            double sum = 0d;
            int count = dataBytes / 2;
            for (int i = 0; i < count; i++)
            {
                short sample = BitConverter.ToInt16(wav, 44 + i * 2);
                float value = sample / 32768f;
                sum += value * value;
            }
            float rms = (float)Math.Sqrt(sum / Math.Max(1, count));
            if (rms < minRms)
            {
                reason = $"silence/low RMS {rms:F4}";
                return false;
            }
            return true;
        }
    }
}
