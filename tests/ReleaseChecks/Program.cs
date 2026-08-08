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

        Check(VoiceSignalMath.HasUsableSignal(0.0011f), "accept observed quiet microphone signal");
        Check(!VoiceSignalMath.HasUsableSignal(0.0001f), "reject true silence floor");
        float quietGain = VoiceSignalMath.CalculateGain(0.0011f, 0.01f);
        Check(quietGain > 10f && quietGain <= 30f, "adaptively amplify quiet microphone");
        Check(VisionIntent.IsVisualQuestion("Buddy, what am I looking at?"), "detect looking-at vision request");
        Check(VisionIntent.IsVisualQuestion("Can you see my screen?"), "detect screen vision request");
        Check(!VisionIntent.IsVisualQuestion("Buddy follow me"), "avoid screenshots for normal commands");

        ShipCommandParsing.ParsePurchase("3 pro flashlights", out string purchaseItem, out int purchaseQuantity);
        Check(purchaseItem == "pro flashlights" && purchaseQuantity == 3, "parse purchase quantity");
        Check(ShipCommandParsing.TryParsePoliteSpawn("please spawn 2 flashlights in front of me", out string spawnItem, out int spawnQuantity) &&
              spawnItem == "flashlights" && spawnQuantity == 2, "parse polite bounded spawn");
        Check(ShipCommandParsing.TryParsePoliteSpawn("please spawn a flashlight for me", out spawnItem, out spawnQuantity) &&
              spawnItem == "flashlight" && spawnQuantity == 1, "parse natural article in polite spawn");
        Check(!ShipCommandParsing.TryParsePoliteSpawn("spawn 2 flashlights", out _, out _), "reject spawn without pleading");
        Check(ShipCommandParsing.TryParsePoliteSpawn("i beg you spawn 99 shovels", out _, out spawnQuantity) && spawnQuantity == 3,
              "cap polite spawn quantity");
        Check(ShipCommandParsing.TryParseFacilityAction("disable turret B3", out string facilityCode, out bool facilityEnable) &&
              facilityCode == "b3" && !facilityEnable, "parse terminal hazard disable");
        Check(ShipCommandParsing.TryParseFacilityAction("open door c7", out _, out facilityEnable) && facilityEnable,
              "parse terminal door open");
        Check(ShipCommandParsing.IsStatusRequest("what time is it?"), "parse ship status question");
        Check(!ShipCommandParsing.IsStatusRequest("what's the weather in Brisbane?"), "do not hijack real-world weather question");

        Check(MovementCommandParsing.Parse("go forward 12 metres").Kind == MovementCommandKind.ScoutAhead &&
              MovementCommandParsing.Parse("go forward 12 metres").ScoutDistance == 12f, "parse bounded scout distance");
        Check(MovementCommandParsing.Parse("check in front").Kind == MovementCommandKind.ScoutAhead, "parse natural scout command");
        Check(MovementCommandParsing.Parse("scout forwards").Kind == MovementCommandKind.ScoutAhead, "parse spoken scout forwards command");
        Check(MovementCommandParsing.Parse("check the next room").Kind == MovementCommandKind.ScoutAhead, "parse next-room scout command");
        Check(MovementCommandParsing.Parse("clear the way").Kind == MovementCommandKind.ScoutAhead, "parse clear-way scout command");
        Check(MovementCommandParsing.Parse("stop following me").Kind == MovementCommandKind.Stay, "stop following is not follow");
        Check(MovementCommandParsing.Parse("stay still").Kind == MovementCommandKind.Stay, "parse verbatim stay-still command");
        Check(MovementCommandParsing.Parse("do not move").Kind == MovementCommandKind.Stay, "preserve negation in stay command");
        Check(MovementCommandParsing.Parse("move forwards").Kind == MovementCommandKind.ScoutAhead, "parse verbatim move-forwards command");
        Check(MovementCommandParsing.Parse("go to the ship").Kind == MovementCommandKind.ReturnToShip, "ship return is not moon route");
        Check(MovementCommandParsing.Parse("what is scrap?").Kind == MovementCommandKind.None, "scrap question is not fetch command");
        Check(MovementCommandParsing.Parse("can you follow me?").Kind == MovementCommandKind.Follow, "parse polite follow command");
        Check(MovementCommandParsing.Parse("ship").Kind == MovementCommandKind.ReturnToShip, "retain short ship command");
        Check(MovementCommandParsing.Parse("no, get off the ship and follow us").Kind == MovementCommandKind.Follow, "parse follow-us correction");
        Check(MovementCommandParsing.Parse("you are not following us").Kind == MovementCommandKind.None, "complaint is not a fresh follow order");

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
