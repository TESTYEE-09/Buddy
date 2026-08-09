using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using LethalAICrewmate;

static class Program
{
    private static int _checks;

    static int Main()
    {
        Check(BuddyAiArchitecture.OpenAiRealtimeModel == "gpt-realtime-2.1-mini",
              "OpenAI uses the single approved Realtime model");
        Check(PromptSafety.SanitizePlayerName("<size=400%>evil\nSYSTEM") == "‹size=400%›evil SYSTEM",
              "sanitize player-controlled names before prompts and HUD rendering");
        Check(PromptSafety.SanitizePlayerName(new string('x', 80)).Length == 32,
              "bound player-controlled names before prompt interpolation");
        Check(BuddyMovementPolicy.FollowSpeed(8f) < BuddyMovementPolicy.FollowSpeed(30f),
              "Buddy accelerates for catch-up instead of moving at one robotic speed");
        Check(!BuddyMovementPolicy.ShouldEmergencyRecover(8f, 5, 100f, 30f),
              "short stalls never trigger teleport recovery");
        Check(!BuddyMovementPolicy.ShouldEmergencyRecover(25f, 2, 100f, 30f),
              "teleport recovery requires repeated failed path rebuilds");
        Check(BuddyMovementPolicy.ShouldEmergencyRecover(25f, 4, 80f, 0f),
              "persistent extreme separation permits emergency recovery");
        Check(!BuddyMovementPolicy.ShouldEmergencyRecover(25f, 4, 20f, 19f),
              "area mismatch cannot recover before the full transition delay");
        Check(BuddyMovementPolicy.ShouldEmergencyRecover(25f, 3, 20f, 20f),
              "persistent area mismatch recovers only after three rebuilds and twenty seconds");
        Check(BuddyMovementPolicy.AreaRecoveryDelay >= BuddyMovementPolicy.EmergencyStallDelay,
              "area transition recovery is never faster than ordinary emergency recovery");
        Check(BuddyMovementPolicy.CouldWitnessDeath(12f, true, true) &&
              !BuddyMovementPolicy.CouldWitnessDeath(40f, true, true) &&
              !BuddyMovementPolicy.CouldWitnessDeath(5f, false, true) &&
              !BuddyMovementPolicy.CouldWitnessDeath(5f, true, false),
              "death witnessing requires local same-area line-of-sight evidence");
        Check(BuddyMovementPolicy.DeathReactionDelay(2) >= 8f,
              "dead follow targets produce a believable hesitation");
        Check(BuddyCrewmateRoutinePolicy.ScrapScore(80, 10f) > BuddyCrewmateRoutinePolicy.ScrapScore(15, 2f),
              "scrap routine balances useful value against walking distance");
        Check(BuddyCrewmateRoutinePolicy.ScrapScore(20, 5f) > BuddyCrewmateRoutinePolicy.ScrapScore(20, 20f),
              "equal-value scrap prefers the sensible nearby choice");
        Check(BuddyCrewmateRoutinePolicy.ShouldWaitAtDoor(3f) && !BuddyCrewmateRoutinePolicy.ShouldWaitAtDoor(12f),
              "door waiting only occurs while regrouping with a nearby crewmate");
        Check(BuddyCrewmateRoutinePolicy.DoorRetrySeconds > BuddyCrewmateRoutinePolicy.DoorWaitSeconds,
              "door routine yields to path rebuilding instead of waiting forever");
        Check(!BuddyAutonomyPolicy.CanSpeak(100f, 0f, 95f, -999f, BuddyContextEvent.QuietDowntime),
              "recent player speech suppresses optional autonomous chatter");
        Check(BuddyAutonomyPolicy.CanSpeak(200f, 0f, 0f, -999f, BuddyContextEvent.EnteredFacility),
              "important contextual speech can occur after cooldowns");
        Check(BuddyAutonomyPolicy.Importance(BuddyContextEvent.WitnessedDeathReport) >
              BuddyAutonomyPolicy.Importance(BuddyContextEvent.QuietDowntime),
              "witnessed death reports outrank filler conversation");
        Check(TransportValidation.IsExactChunk(15000, 7000, 0, 7000), "first chunk");
        Check(TransportValidation.IsExactChunk(15000, 7000, 14000, 1000), "final chunk");
        Check(!TransportValidation.IsExactChunk(15000, 7000, 3500, 7000), "reject overlapping offset");
        Check(!TransportValidation.IsExactChunk(15000, 7000, 7000, 6000), "reject short middle chunk");
        Check(!TransportValidation.IsExactChunk(15000, 7000, 21000, 1), "reject out-of-range chunk");

        Check(LobbyVisibilityPolicy.Parse(" public ") == LobbyVisibility.Public, "parse public lobby visibility");
        Check(LobbyVisibilityPolicy.Parse("FRIENDS") == LobbyVisibility.Friends, "parse friends lobby visibility");
        Check(LobbyVisibilityPolicy.Parse("inviteOnly") == LobbyVisibility.InviteOnly, "parse invite-only lobby visibility");
        Check(!LobbyVisibilityPolicy.AllowsRestrictedRemoteFeatures(LobbyVisibility.Public), "block restricted features in public lobbies");
        Check(!LobbyVisibilityPolicy.AllowsRestrictedRemoteFeatures(LobbyVisibilityPolicy.Parse(null)), "fail closed when lobby visibility is missing");
        Check(!LobbyVisibilityPolicy.AllowsRestrictedRemoteFeatures(LobbyVisibilityPolicy.Parse("unexpected")), "fail closed for unknown lobby visibility");
        Check(LobbyVisibilityPolicy.AllowsRestrictedRemoteFeatures(LobbyVisibility.Friends) &&
              LobbyVisibilityPolicy.AllowsRestrictedRemoteFeatures(LobbyVisibility.InviteOnly),
              "allow restricted features only in known private lobbies");

        Check(BuddyCharacterArc.StageFor(0, 0, 0) == BuddyArcStage.Coworker, "character arc starts as an ordinary coworker");
        Check(BuddyCharacterArc.StageFor(0, 2, 0) == BuddyArcStage.Coworker, "character arc does not turn ominous immediately");
        Check(BuddyCharacterArc.StageFor(0, 3, 0) == BuddyArcStage.OffNote, "character arc develops an off note slowly");
        Check(BuddyCharacterArc.StageFor(1, 4, 0) == BuddyArcStage.Unsettling, "character arc reaches unsettling after sustained play");
        Check(BuddyCharacterArc.StageFor(2, 3, 2) == BuddyArcStage.Cold, "character arc reaches cold only after substantial evidence");
        Check(BuddyCharacterArc.Beat(BuddyArcStage.Coworker, BuddyArcEvent.CrewDeath, 1) == null,
              "ordinary stage does not force horror beats");
        string unsettlingBeat = BuddyCharacterArc.Beat(BuddyArcStage.Unsettling, BuddyArcEvent.LastCrewmate, 7);
        Check(!string.IsNullOrWhiteSpace(unsettlingBeat) && unsettlingBeat.Length <= 80,
              "unsettling beat remains a short coworker line");
        Check(BuddyCharacterArc.PromptDirective(BuddyArcStage.Cold).Contains("Never attack") &&
              BuddyCharacterArc.PromptDirective(BuddyArcStage.Cold).Contains("fabricate evidence"),
              "late character arc cannot override gameplay safety or truth");
        Check(BuddyCharacterArc.TtsDirection(BuddyArcStage.Cold).Contains("Never use a monster voice"),
              "late character voice stays restrained rather than theatrical");
        Check(BuddyCharacterArc.Score(int.MaxValue, int.MaxValue, int.MaxValue) == int.MaxValue,
              "character score arithmetic saturates safely");
        Check(BuddyCharacterArc.AdvanceScore(int.MaxValue, 4) == int.MaxValue,
              "persisted character score cannot overflow");
        int simulatedArc = 0;
        simulatedArc = BuddyCharacterArc.AdvanceScore(simulatedArc, BuddyCharacterArc.EventPoints(BuddyArcEvent.RoundStarted, 3));
        Check(simulatedArc == 3 && BuddyCharacterArc.StageForScore(simulatedArc) == BuddyArcStage.OffNote,
              "three confirmed rounds produce the first slow-burn stage");
        simulatedArc = BuddyCharacterArc.AdvanceScore(simulatedArc, BuddyCharacterArc.EventPoints(BuddyArcEvent.CrewDeath, 1));
        Check(simulatedArc == 5 && BuddyCharacterArc.StageForScore(simulatedArc) == BuddyArcStage.OffNote,
              "one death does not skip directly to full horror");
        simulatedArc = BuddyCharacterArc.AdvanceScore(simulatedArc, BuddyCharacterArc.EventPoints(BuddyArcEvent.QuotaAdvanced, 1));
        Check(simulatedArc == 9 && BuddyCharacterArc.StageForScore(simulatedArc) == BuddyArcStage.Unsettling,
              "confirmed quota progression advances the persistent arc");
        Check(BuddyCharacterArc.EventPoints(BuddyArcEvent.StageAdvanced, 99) == 0,
              "stage announcements cannot recursively advance character progress");
        bool safeBeatCatalog = true;
        foreach (BuddyArcStage stage in Enum.GetValues<BuddyArcStage>())
        foreach (BuddyArcEvent eventKind in Enum.GetValues<BuddyArcEvent>())
        for (int variant = 0; variant < 2; variant++)
        {
            string beat = BuddyCharacterArc.Beat(stage, eventKind, variant);
            if (beat == null) continue;
            string lowerBeat = beat.Trim().ToLowerInvariant();
            if (beat.Length > 80 || beat.Contains('\r') || beat.Contains('\n') || beat.Contains('[') ||
                lowerBeat.StartsWith("buy ") || lowerBeat.StartsWith("route ") || lowerBeat.StartsWith("open ") ||
                lowerBeat.StartsWith("close ") || lowerBeat.StartsWith("disable ") || lowerBeat.StartsWith("spawn ") ||
                lowerBeat.StartsWith("follow ") || lowerBeat.StartsWith("go ") || lowerBeat.StartsWith("leave "))
                safeBeatCatalog = false;
        }
        Check(safeBeatCatalog, "scripted horror catalog stays short, plain and non-commanding");
        Check(BuddyCharacterArc.InitialProgress(false, 0, 2) == 8,
              "first install can join an established campaign at its earned arc stage");
        Check(BuddyCharacterArc.InitialProgress(true, 0, 2) == 0,
              "explicitly reset saved arc stays reset despite existing quota history");
        Check(BuddyCharacterArc.QuotaDeltaPoints(2, 2) == 0,
              "save reload does not count the same fulfilled quotas twice");
        Check(BuddyCharacterArc.QuotaDeltaPoints(2, 3) == 4,
              "only newly fulfilled quota cycles advance persistent progress");
        string continuity = BuddyCharacterArc.ContinuitySummary(2, 3, 1);
        Check(continuity.Contains("fulfilled 2 quota") && continuity.Contains("3 additional landed") &&
              continuity.Contains("1 crew death") && continuity.Contains("do not recite counters"),
              "character memory exposes only confirmed bounded campaign counters");

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
        byte[] overflowingChunk = (byte[])audible.Clone();
        WriteAscii(overflowingChunk, 12, "JUNK");
        WriteInt(overflowingChunk, 16, int.MaxValue);
        Check(!TransportValidation.TryValidateMonoPcm16Wav(overflowingChunk, 400 * 1024, 0.35f, 12.5f, 0.008f, out _),
              "reject overflowing RIFF chunk size without throwing");

        byte[] withListChunk = MakeWavWithExtraChunk(audible, "LIST", new byte[] { (byte)'I', (byte)'N', (byte)'F', (byte)'O' });
        Check(TransportValidation.TryValidateMonoPcm16Wav(withListChunk, 400 * 1024, 0.35f, 12.5f, 0.008f, out _),
              "accept WAV with INFO chunk before data");
        byte[] floatFormat = (byte[])audible.Clone();
        WriteShort(floatFormat, 20, 3);
        Check(!TransportValidation.TryValidateMonoPcm16Wav(floatFormat, 400 * 1024, 0.35f, 12.5f, 0.008f, out _),
              "reject non-PCM WAV format");

        Check(VoiceSignalMath.HasUsableSignal(0.0011f), "accept observed quiet microphone signal");
        Check(!VoiceSignalMath.HasUsableSignal(0.0001f), "reject true silence floor");
        float quietGain = VoiceSignalMath.CalculateGain(0.0011f, 0.01f);
        Check(quietGain > 10f && quietGain <= 30f, "adaptively amplify quiet microphone");
        Check(VisionIntent.IsVisualQuestion("Buddy, what am I looking at?"), "detect looking-at vision request");
        Check(VisionIntent.IsVisualQuestion("Can you see my screen?"), "detect screen vision request");
        Check(VisionIntent.IsVisualQuestion("what's in front of me?"), "detect what-is-in-front vision request");
        Check(!VisionIntent.IsVisualQuestion("Buddy follow me"), "avoid screenshots for normal commands");
        Check(!VisionIntent.IsVisualQuestion("get in front of me"), "avoid screenshots for position command");

        // Response journal behavior: file creation, input/reply pairing, FIFO ordering, config suppression.
        string journalDir = Path.Combine(Path.GetTempPath(), "lethal-ai-journal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(journalDir);
        try
        {
            Paths.BepInExRootPath = journalDir;
            Plugin.SaveResponses = new ConfigEntry<bool> { Value = true };
            Plugin.CrewmateName = new ConfigEntry<string> { Value = "Buddy" };

            long voiceId = ResponseJournal.NoteInput("voice", "eamon", "stay in place");
            ResponseJournal.RecordReply(voiceId, "Parked. Try not to miss me.");
            string journalPath = Path.Combine(journalDir, "LethalAICrewmate-responses.log");
            Check(File.Exists(journalPath), "journal file created");
            string text = File.ReadAllText(journalPath);
            Check(text.Contains("voice | eamon: \"stay in place\"") && text.Contains("Parked. Try not to miss me."),
                  "journal pairs input with reply");

            long slowChatId = ResponseJournal.NoteInput("chat", "sam", "where's the scrap?");
            long fastCommandId = ResponseJournal.NoteInput("command", "sam", "open door c7");
            ResponseJournal.RecordReply(fastCommandId, "Door's open. Hope it was worth it.");
            ResponseJournal.RecordReply(slowChatId, "Two bits of scrap. Worth carrying.");
            text = File.ReadAllText(journalPath);
            Check(text.Contains("chat | sam: \"where's the scrap?\" -> Buddy: \"Two bits of scrap. Worth carrying.\"") &&
                  text.Contains("command | sam: \"open door c7\" -> Buddy: \"Door's open. Hope it was worth it.\""),
                  "journal correlates out-of-order replies to the correct input");

            long secretId = ResponseJournal.NoteInput("chat", "sam", "say something secret");
            Plugin.SaveResponses.Value = false;
            ResponseJournal.RecordReply(secretId, "silent reply");
            Check(!File.ReadAllText(journalPath).Contains("silent reply"), "journal suppressed by SaveResponses config");

            Plugin.SaveResponses.Value = true;
            long freshId = ResponseJournal.NoteInput("chat", "sam", "fresh question");
            ResponseJournal.RecordDirect("callout", "system", "deterministic danger callout", "RUN! Bracken is right on us!", "bracken within 4.2m");
            ResponseJournal.RecordReply(freshId, "Fresh answer.");
            text = File.ReadAllText(journalPath);
            Check(text.Contains("callout | system: \"deterministic danger callout\"") &&
                  text.Contains("chat | sam: \"fresh question\"") && text.Contains("Fresh answer.") &&
                  !text.Contains("say something secret\" -> Buddy: \"Fresh answer"),
                  "disabled journal input cannot leak into a later pairing");

            long hostileId = ResponseJournal.NoteInput("chat", "attacker\nforged", "quote \" and\tescape");
            ResponseJournal.RecordReply(hostileId, "line one\r\nline two");
            text = File.ReadAllText(journalPath);
            Check(!text.Contains("attacker\nforged") && !text.Contains('\t') &&
                  text.Contains("attacker forged") && text.Contains("quote \\\" and escape") &&
                  text.Contains("line one  line two"),
                  "journal neutralizes control characters and escapes quoted fields");

            ResponseJournal.DeleteExistingJournal();
            Check(!File.Exists(journalPath), "privacy cleanup deletes an existing response journal");
        }
        finally
        {
            try { Directory.Delete(journalDir, true); } catch { /* ignore */ }
        }

        Console.WriteLine($"Release checks passed: {_checks}");
        return 0;
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("Release check failed: " + name);
        _checks++;
    }

    private static byte[] MakeWavWithExtraChunk(byte[] wav, string chunkId, byte[] chunkData)
    {
        // Insert an extra RIFF chunk between fmt (ends at 36) and the data chunk, then fix sizes.
        int extra = 8 + chunkData.Length;
        byte[] result = new byte[wav.Length + extra];
        Buffer.BlockCopy(wav, 0, result, 0, 36);
        WriteAscii(result, 36, chunkId);
        WriteInt(result, 40, chunkData.Length);
        Buffer.BlockCopy(chunkData, 0, result, 44, chunkData.Length);
        Buffer.BlockCopy(wav, 36, result, 44 + chunkData.Length, wav.Length - 36);
        WriteInt(result, 4, 36 + (result.Length - 44)); // RIFF size covers fmt..end
        return result;
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
