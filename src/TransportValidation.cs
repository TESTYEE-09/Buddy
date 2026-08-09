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

            // Walk the RIFF chunks instead of assuming the data chunk sits at offset 44.
            // Valid encoders may emit LIST/INFO or other chunks before it.
            int dataOffset = -1;
            int dataBytes = -1;
            int channels = 0;
            int sampleRate = 0;
            int bits = 0;
            for (int at = 12; at + 8 <= wav.Length;)
            {
                int size = BitConverter.ToInt32(wav, at + 4);
                int payloadOffset = at + 8;
                if (size < 0 || size > wav.Length - payloadOffset)
                {
                    reason = "corrupt RIFF chunk";
                    return false;
                }
                if (wav[at] == 'd' && wav[at + 1] == 'a' && wav[at + 2] == 't' && wav[at + 3] == 'a')
                {
                    dataOffset = at + 8;
                    dataBytes = size;
                    break;
                }
                if (wav[at] == 'f' && wav[at + 1] == 'm' && wav[at + 2] == 't' && wav[at + 3] == ' ' && size >= 16)
                {
                    int format = BitConverter.ToInt16(wav, at + 8);
                    channels = BitConverter.ToInt16(wav, at + 10);
                    sampleRate = BitConverter.ToInt32(wav, at + 12);
                    bits = BitConverter.ToInt16(wav, at + 22);
                    if (format != 1)
                    {
                        reason = "non-PCM WAV format";
                        return false;
                    }
                }
                int next = payloadOffset + size;
                if ((size & 1) != 0)
                {
                    if (next >= wav.Length)
                    {
                        reason = "corrupt RIFF chunk padding";
                        return false;
                    }
                    next++;
                }
                at = next;
            }

            if (dataOffset < 0 || dataBytes <= 0 || dataOffset + dataBytes > wav.Length)
            {
                reason = "missing or inconsistent data chunk";
                return false;
            }
            if (channels != 1 || bits != 16 || sampleRate < 8000 || sampleRate > 48000 || (dataBytes & 1) != 0)
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
                short sample = BitConverter.ToInt16(wav, dataOffset + i * 2);
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
