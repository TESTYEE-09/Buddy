using System;
using System.IO;
using System.Runtime.CompilerServices;

internal static class StreamingVoiceRegressionChecks
{
    [ModuleInitializer]
    internal static void Run()
    {
        string root = Directory.GetCurrentDirectory();
        string realtime = Read(root, "src", "OpenAiRealtimeVoiceClient.cs");
        string hostVoice = Read(root, "src", "VoiceCommand.cs");
        string remoteVoice = Read(root, "src", "BuddyClientVoice.cs");
        string encoder = Read(root, "src", "StreamingMicCapture.cs");

        Require(realtime.Contains("TryBeginStreamingVoice", StringComparison.Ordinal) &&
                realtime.Contains("AppendStreamingVoice", StringComparison.Ordinal) &&
                realtime.Contains("EndStreamingVoice", StringComparison.Ordinal) &&
                realtime.Contains("AbortStreamingVoice", StringComparison.Ordinal),
                "Realtime PTT must expose an explicit begin/append/commit/abort streaming lifecycle");

        Require(realtime.Contains("turn.Stream.Signal.WaitAsync", StringComparison.Ordinal) &&
                realtime.Contains("input_audio_buffer.append", StringComparison.Ordinal) &&
                realtime.Contains("input_audio_buffer.commit", StringComparison.Ordinal) &&
                realtime.Contains("releaseToCommitMs", StringComparison.Ordinal),
                "OpenAI input audio must be appended while PTT is held and only committed on release");

        Require(realtime.Contains("_processingTurn && !_responseActive", StringComparison.Ordinal) &&
                realtime.Contains("foreach (VoiceTurn queued in Pending)", StringComparison.Ordinal) &&
                realtime.Contains("if (queued.Stream != null) return false", StringComparison.Ordinal),
                "overlapping speakers must not erase a committed turn or enter unsafe response setup gaps");

        Require(hostVoice.Contains("FlushStreamingAudio(false)", StringComparison.Ordinal) &&
                hostVoice.Contains("AppendStreamingVoice(_streamId, pcm)", StringComparison.Ordinal) &&
                hostVoice.Contains("EndStreamingVoice(_streamId)", StringComparison.Ordinal) &&
                !hostVoice.Contains("EncodeAdaptiveMonoWav", StringComparison.Ordinal),
                "host PTT must stream microphone chunks before release instead of uploading a completed WAV");

        Require(remoteVoice.Contains("MsgVoiceEnd", StringComparison.Ordinal) &&
                remoteVoice.Contains("ReliableFragmentedSequenced", StringComparison.Ordinal) &&
                remoteVoice.Contains("offset != incoming.ReceivedBytes", StringComparison.Ordinal) &&
                remoteVoice.Contains("MaxIncomingTransfers = 1", StringComparison.Ordinal),
                "remote voice relay must stay fragmented, ordered and single-owner");

        Require(remoteVoice.Contains("ResolveRemotePlayer(senderId)", StringComparison.Ordinal) &&
                Count(remoteVoice, "IsSenderInBuddyRange(senderId)") >= 2 &&
                remoteVoice.Contains("NetMessenger.IsCompatibleClient(senderId)", StringComparison.Ordinal) &&
                !remoteVoice.Contains("OpenAiSecrets.CurrentKey", StringComparison.Ordinal) &&
                !remoteVoice.Contains("ClientWebSocket", StringComparison.Ordinal),
                "remote clients must be server-identified, compatibility/range checked, and never own provider credentials");

        Require(!remoteVoice.Contains("EncodeAdaptiveMonoWav", StringComparison.Ordinal) &&
                !remoteVoice.Contains("HostQueue", StringComparison.Ordinal) &&
                !remoteVoice.Contains("RemoteVoiceRequest", StringComparison.Ordinal),
                "remote PTT must not regress to buffering a complete WAV before host upload");

        Require(encoder.Contains("WireRate = 16000", StringComparison.Ordinal) &&
                encoder.Contains("ChunkMilliseconds = 100", StringComparison.Ordinal) &&
                encoder.Contains("targetSamples &= ~1", StringComparison.Ordinal),
                "live microphone transport must remain bounded 16 kHz PCM in stable 100 ms chunks");
    }

    private static string Read(string root, params string[] parts)
    {
        string path = root;
        foreach (string part in parts) path = Path.Combine(path, part);
        if (!File.Exists(path)) throw new InvalidOperationException("release checks could not locate " + path);
        return File.ReadAllText(path);
    }

    private static int Count(string text, string needle)
    {
        int count = 0;
        int at = 0;
        while ((at = text.IndexOf(needle, at, StringComparison.Ordinal)) >= 0)
        {
            count++;
            at += needle.Length;
        }
        return count;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Streaming voice regression check failed: " + message);
    }
}
