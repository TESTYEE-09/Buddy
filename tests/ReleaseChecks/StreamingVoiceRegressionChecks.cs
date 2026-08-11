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
        string net = Read(root, "src", "NetMessenger.cs");

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
                hostVoice.Contains("AbortAllStreamingVoices", StringComparison.Ordinal) &&
                !hostVoice.Contains("EncodeAdaptiveMonoWav", StringComparison.Ordinal),
                "host PTT must stream microphone chunks before release instead of uploading a completed WAV");

        Require(remoteVoice.Contains("MsgVoiceEnd", StringComparison.Ordinal) &&
                Count(remoteVoice, "NetworkDelivery.ReliableFragmentedSequenced") >= 3 &&
                !remoteVoice.Contains("NetworkDelivery.ReliableSequenced", StringComparison.Ordinal) &&
                remoteVoice.Contains("offset != incoming.ReceivedBytes", StringComparison.Ordinal) &&
                remoteVoice.Contains("MaxIncomingTransfers = 1", StringComparison.Ordinal),
                "remote start/chunk/end must share one fragmented ordered pipeline and one microphone owner");

        Require(remoteVoice.Contains("ResolveRemotePlayer(senderId)", StringComparison.Ordinal) &&
                Count(remoteVoice, "IsSenderInBuddyRange(senderId)") >= 2 &&
                remoteVoice.Contains("NetMessenger.IsCompatibleClient(senderId)", StringComparison.Ordinal) &&
                remoteVoice.Contains("SenderCooldownSeconds = 3f", StringComparison.Ordinal) &&
                !remoteVoice.Contains("OpenAiSecrets.CurrentKey", StringComparison.Ordinal) &&
                !remoteVoice.Contains("ClientWebSocket", StringComparison.Ordinal),
                "remote clients must be server-identified, rate/range checked, and never own provider credentials");

        Require(!remoteVoice.Contains("EncodeAdaptiveMonoWav", StringComparison.Ordinal) &&
                !remoteVoice.Contains("HostQueue", StringComparison.Ordinal) &&
                !remoteVoice.Contains("RemoteVoiceRequest", StringComparison.Ordinal),
                "remote PTT must not regress to buffering a complete WAV before host upload");

        Require(encoder.Contains("WireRate = 16000", StringComparison.Ordinal) &&
                encoder.Contains("ChunkMilliseconds = 100", StringComparison.Ordinal) &&
                encoder.Contains("targetSamples &= ~1", StringComparison.Ordinal),
                "live microphone transport must remain bounded 16 kHz PCM in stable 100 ms chunks");

        int cancelStart = realtime.IndexOf("private static async Task TrySendCancelAsync", StringComparison.Ordinal);
        int cancelEnd = realtime.IndexOf("private static async Task RunWorkerAsync", StringComparison.Ordinal);
        string cancelMethod = cancelStart >= 0 && cancelEnd > cancelStart
            ? realtime.Substring(cancelStart, cancelEnd - cancelStart)
            : "";
        Require(realtime.Contains("if (!_responseCancelRequested) return", StringComparison.Ordinal) &&
                realtime.Contains("await _socket.SendAsync(new ArraySegment<byte>(cancel)", StringComparison.Ordinal) &&
                !cancelMethod.Contains("input_audio_buffer.clear", StringComparison.Ordinal),
                "a stale cancellation must re-check its flag under the send lock and never clear the next turn's input buffer");

        Require(remoteVoice.Contains("wrapped; aborting Buddy capture", StringComparison.Ordinal) &&
                remoteVoice.Contains("exceeded the bounded upload size.", StringComparison.Ordinal) &&
                remoteVoice.Contains("if (!_clientRecording) return;", StringComparison.Ordinal) &&
                hostVoice.Contains("wrapped; aborting Buddy capture", StringComparison.Ordinal) &&
                hostVoice.Contains("stopped accepting microphone audio.", StringComparison.Ordinal) &&
                hostVoice.Contains("if (!_recording) return;", StringComparison.Ordinal),
                "host and remote PTT must abort the capture gracefully instead of throwing mid-stream every frame");

        Require(net.Contains("ProtocolVersion = 8", StringComparison.Ordinal),
                "the incompatible live PTT wire format must stay behind its bumped multiplayer protocol gate");
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
