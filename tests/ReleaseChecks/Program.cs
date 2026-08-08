using System;
using LethalAICrewmate;

static class Program
{
    private static int _checks;

    static int Main()
    {
        Check(TransportValidation.IsExactChunk(15000, 7000, 0, 7000), "first chunk");
        Check(TransportValidation.IsExactChunk(15000, 7000, 14000, 1000), "final chunk");
        Check(!TransportValidation.IsExactChunk(15000, 7000, 3500, 7000), "reject overlapping offset");
        Check(!TransportValidation.IsExactChunk(15000, 7000, 7000, 6000), "reject short middle chunk");
        Check(!TransportValidation.IsExactChunk(15000, 7000, 21000, 1), "reject out-of-range chunk");

        byte[] audible = MakeWav(16000, 1f, 0.12f);
        Check(TransportValidation.TryValidateMonoPcm16Wav(audible, 400 * 1024, 0.35f, 12.5f, 0.008f, out _), "accept bounded audible WAV");
        byte[] silence = MakeWav(16000, 1f, 0f);
        Check(!TransportValidation.TryValidateMonoPcm16Wav(silence, 400 * 1024, 0.35f, 12.5f, 0.008f, out _), "reject silence");
        byte[] malformed = (byte[])audible.Clone();
        malformed[0] = (byte)'X';
        Check(!TransportValidation.TryValidateMonoPcm16Wav(malformed, 400 * 1024, 0.35f, 12.5f, 0.008f, out _), "reject malformed header");
        byte[] truncated = new byte[audible.Length - 2];
        Buffer.BlockCopy(audible, 0, truncated, 0, truncated.Length);
        Check(!TransportValidation.TryValidateMonoPcm16Wav(truncated, 400 * 1024, 0.35f, 12.5f, 0.008f, out _), "reject inconsistent data length");

        Console.WriteLine($"Release checks passed: {_checks}");
        return 0;
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("Release check failed: " + name);
        _checks++;
    }

    private static byte[] MakeWav(int rate, float seconds, float amplitude)
    {
        int samples = (int)(rate * seconds);
        int dataLength = samples * 2;
        byte[] wav = new byte[44 + dataLength];
        WriteAscii(wav, 0, "RIFF");
        WriteInt(wav, 4, 36 + dataLength);
        WriteAscii(wav, 8, "WAVE");
        WriteAscii(wav, 12, "fmt ");
        WriteInt(wav, 16, 16);
        WriteShort(wav, 20, 1);
        WriteShort(wav, 22, 1);
        WriteInt(wav, 24, rate);
        WriteInt(wav, 28, rate * 2);
        WriteShort(wav, 32, 2);
        WriteShort(wav, 34, 16);
        WriteAscii(wav, 36, "data");
        WriteInt(wav, 40, dataLength);
        for (int i = 0; i < samples; i++)
            WriteShort(wav, 44 + i * 2, (short)(Math.Sin(i * 0.1) * amplitude * short.MaxValue));
        return wav;
    }

    private static void WriteAscii(byte[] target, int offset, string value) =>
        System.Text.Encoding.ASCII.GetBytes(value).CopyTo(target, offset);

    private static void WriteInt(byte[] target, int offset, int value) =>
        BitConverter.GetBytes(value).CopyTo(target, offset);

    private static void WriteShort(byte[] target, int offset, short value) =>
        BitConverter.GetBytes(value).CopyTo(target, offset);
}
