using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace LethalAICrewmate
{
    /// <summary>
    /// Persistent host-side OpenAI Realtime session for conversation, native voice, images and
    /// tool calling. This is Buddy's complete OpenAI path; no REST chat or separate TTS model is used.
    /// </summary>
    internal static class OpenAiRealtimeVoiceClient
    {
        private const int OutputRate = 24000;
        private const int InputWireRate = 16000;
        private const int MaxQueuedTurns = 3;
        private const int IdleTimeoutSeconds = 20;
        private const int MaxLiveInputBytes = OutputRate * 2 * 14;
        private static readonly ConcurrentQueue<Action> MainThread = new ConcurrentQueue<Action>();
        private static readonly Queue<VoiceTurn> Pending = new Queue<VoiceTurn>();
        private static readonly object Gate = new object();
        private static bool _workerRunning;
        private static bool _processingTurn;
        // A response only exists after response.create has been sent and before response.done.
        // Keep this separate from the websocket state: pressing PTT between turns must not send
        // response.cancel, because the Realtime API correctly rejects that with "no active response".
        private static bool _responseActive;
        private static bool _responseCancelRequested;
        private static ClientWebSocket _socket;
        private static CancellationTokenSource _sessionCancel;
        private static readonly SemaphoreSlim SendLock = new SemaphoreSlim(1, 1);
        // The session config currently applied to the open socket. Instructions and tool
        // definitions sit at the head of every request, so re-sending them per turn would bust the
        // prompt cache for the rest of the session; they are pushed only when they actually change.
        private static string _appliedSessionConfig;
        private static LiveVoiceInput _liveInput;
        private static readonly Dictionary<ulong, LiveVoiceInput> TrackedLiveInputs = new Dictionary<ulong, LiveVoiceInput>();
        private static ulong _nextLiveInputId = 1;

        private sealed class LiveVoiceInput
        {
            public readonly object Gate = new object();
            public readonly Queue<byte[]> Chunks = new Queue<byte[]>();
            public readonly SemaphoreSlim Signal = new SemaphoreSlim(0);
            public readonly CancellationTokenSource CommitCancellation = new CancellationTokenSource();
            public ulong Id;
            public bool Ended;
            public bool Cancelled;
            public int BufferedBytes;
            public DateTime StartedUtc;
            public DateTime ReleasedUtc;
            public bool FirstAudioLogged;
            public bool IsRemote;
        }

        private sealed class VoiceTurn
        {
            public byte[] Pcm24k;
            public LiveVoiceInput Stream;
            public string Text;
            public int PlayerId;
            public string PlayerName;
            public string TurnContext;
            public string Contract;
            public long JournalId;
            public bool AllowTools;
            public string MemoryInput;
        }

        private sealed class PendingToolCall
        {
            public string Name;
            public string CallId;
            public string Arguments;
        }

        internal static bool Enabled => true;

        internal static void Tick()
        {
            while (MainThread.TryDequeue(out Action action))
            {
                try { action(); }
                catch (Exception ex) { Plugin.Log?.LogWarning("Realtime main-thread action: " + ex.Message); }
            }
        }

        internal static bool TryBeginStreamingVoice(int playerId, string playerName, out ulong streamId, bool isRemote = false)
        {
            streamId = 0;
            if (!Enabled || !OpenAiSecrets.HasKey) return false;

            string safeName = PromptSafety.SanitizePlayerName(playerName);
            LiveVoiceInput live;
            bool cancelActiveResponse;
            lock (Gate)
            {
                // One live microphone owns the single Realtime input buffer at a time. A new direct
                // speaker may interrupt an active Buddy response, but never start in the tiny setup,
                // commit or tool-result gaps where no response exists yet to cancel safely.
                if (_liveInput != null || (_processingTurn && !_responseActive)) return false;

                // EndStreamingVoice releases the live-input reservation immediately so the finished
                // microphone cannot block forever. If its turn is still queued, preserve it rather
                // than letting a second speaker clear and silently drop the just-committed speech.
                foreach (VoiceTurn queued in Pending)
                    if (queued.Stream != null) return false;

                while (Pending.Count > 0)
                    ResponseJournal.Discard(Pending.Dequeue().JournalId);

                ulong id = _nextLiveInputId++;
                if (_nextLiveInputId == 0) _nextLiveInputId = 1;
                live = new LiveVoiceInput
                {
                    Id = id,
                    StartedUtc = DateTime.UtcNow,
                    IsRemote = isRemote
                };
                _liveInput = live;
                TrackedLiveInputs[id] = live;
                Pending.Enqueue(new VoiceTurn
                {
                    Stream = live,
                    PlayerId = playerId,
                    PlayerName = safeName,
                    TurnContext = BuddyConversationPrompt.BuildTurnContext(safeName, playerId),
                    Contract = BuddyConversationPrompt.BuildContract(),
                    AllowTools = true,
                    MemoryInput = "[voice input understood directly by gpt-realtime-2.1-mini]"
                });

                cancelActiveResponse = _responseActive;
                if (cancelActiveResponse) _responseCancelRequested = true;
                if (!_workerRunning)
                {
                    _workerRunning = true;
                    _ = RunWorkerAsync();
                }
                streamId = id;
            }

            MainThread.Enqueue(BuddyNetworkAudio.StopPlayback);
            if (cancelActiveResponse && _socket != null && _socket.State == WebSocketState.Open)
                _ = TrySendCancelAsync();
            return true;
        }

        internal static bool AppendStreamingVoice(ulong streamId, byte[] pcm16k)
        {
            if (streamId == 0 || pcm16k == null || pcm16k.Length < 4 || (pcm16k.Length & 1) != 0)
                return false;
            if (!TryConvertPcm16kToPcm24k(pcm16k, out byte[] pcm24k)) return false;

            LiveVoiceInput live;
            lock (Gate)
            {
                live = _liveInput;
                // Every one of these refusals surfaces the same player-facing "live voice stream
                // stopped" line, so without naming the cause here the report is undiagnosable from
                // an ordinary log. Each branch says which of the four it was.
                if (live == null)
                {
                    Plugin.Log?.LogWarning($"Realtime live input {streamId} rejected: no live input is reserved (session reset or turn aborted mid-capture).");
                    return false;
                }
                if (live.Id != streamId)
                {
                    Plugin.Log?.LogWarning($"Realtime live input {streamId} rejected: superseded by stream {live.Id}.");
                    return false;
                }
            }

            bool overflow = false;
            lock (live.Gate)
            {
                if (live.Cancelled || live.Ended)
                {
                    Plugin.Log?.LogWarning($"Realtime live input {streamId} rejected: stream already {(live.Cancelled ? "cancelled" : "ended")} (its turn failed or was aborted while the key was still held).");
                    return false;
                }
                if (live.BufferedBytes + pcm24k.Length > MaxLiveInputBytes)
                {
                    live.Cancelled = true;
                    live.Ended = true;
                    live.Chunks.Clear();
                    live.BufferedBytes = 0;
                    live.Signal.Release();
                    overflow = true;
                }
                else
                {
                    live.Chunks.Enqueue(pcm24k);
                    live.BufferedBytes += pcm24k.Length;
                    live.Signal.Release();
                }
            }

            if (!overflow) return true;
            // Never take the global state lock while holding the per-stream lock. Keeping one lock
            // order avoids a future Append/End deadlock if a caller moves off Unity's main thread.
            lock (Gate)
                if (_liveInput == live) _liveInput = null;
            Plugin.Log?.LogWarning($"Realtime live input {streamId} rejected: exceeded the {MaxLiveInputBytes} byte queue bound, so the worker never drained it (the turn was still blocked connecting or on a previous reply).");
            return false;
        }

        internal static bool EndStreamingVoice(ulong streamId)
        {
            if (streamId == 0) return false;
            LiveVoiceInput live;
            lock (Gate)
            {
                live = _liveInput;
                if (live == null || live.Id != streamId) return false;
                _liveInput = null;
            }
            lock (live.Gate)
            {
                if (live.Cancelled || live.Ended) return false;
                live.Ended = true;
                live.ReleasedUtc = DateTime.UtcNow;
                live.Signal.Release();
                return true;
            }
        }

        internal static void AbortStreamingVoice(ulong streamId)
        {
            if (streamId == 0) return;
            LiveVoiceInput live = null;
            lock (Gate)
            {
                TrackedLiveInputs.TryGetValue(streamId, out live);
                if (_liveInput == live) _liveInput = null;
            }
            if (live == null) return;
            CancelLiveInput(live);
        }

        internal static void AbortRemoteStreamingVoices()
        {
            AbortTrackedStreamingVoices(remoteOnly: true);
        }

        internal static void AbortAllStreamingVoices()
        {
            AbortTrackedStreamingVoices(remoteOnly: false);
        }

        private static void AbortTrackedStreamingVoices(bool remoteOnly)
        {
            var matches = new List<LiveVoiceInput>();
            lock (Gate)
            {
                foreach (LiveVoiceInput live in TrackedLiveInputs.Values)
                    if (live != null && (!remoteOnly || live.IsRemote)) matches.Add(live);
                if (_liveInput != null && (!remoteOnly || _liveInput.IsRemote)) _liveInput = null;
            }
            foreach (LiveVoiceInput live in matches) CancelLiveInput(live);
        }

        private static void CancelLiveInput(LiveVoiceInput live)
        {
            if (live == null) return;
            lock (live.Gate)
            {
                if (live.Cancelled) return;
                live.Cancelled = true;
                live.Ended = true;
                live.Chunks.Clear();
                live.BufferedBytes = 0;
                try { live.CommitCancellation.Cancel(); } catch { }
                live.Signal.Release();
            }
        }

        private static bool IsLiveInputCancelled(LiveVoiceInput live)
        {
            if (live == null) return true;
            lock (live.Gate) return live.Cancelled;
        }

        internal static bool EnqueueWav(byte[] wav, int playerId, string playerName)
        {
            if (!Enabled || wav == null || !TryConvertWavToPcm24k(wav, out byte[] pcm)) return false;
            string safeName = PromptSafety.SanitizePlayerName(playerName);
            return EnqueueTurn(new VoiceTurn
            {
                Pcm24k = pcm,
                PlayerId = playerId,
                PlayerName = safeName,
                TurnContext = BuddyConversationPrompt.BuildTurnContext(safeName, playerId),
                Contract = BuddyConversationPrompt.BuildContract(),
                AllowTools = true,
                MemoryInput = "[voice input understood directly by gpt-realtime-2.1-mini]"
            });
        }

        internal static bool EnqueueText(string text, string playerName, int playerId, long journalId, bool allowTools)
        {
            if (!Enabled || string.IsNullOrWhiteSpace(text)) return false;
            string safeName = PromptSafety.SanitizePlayerName(playerName);
            return EnqueueTurn(new VoiceTurn
            {
                Text = text.Trim(),
                PlayerId = playerId,
                PlayerName = safeName,
                // A game observation has no human speaker; naming one invites Buddy to reply to it.
                TurnContext = BuddyConversationPrompt.BuildTurnContext(playerId >= 0 ? safeName : null, playerId),
                Contract = BuddyConversationPrompt.BuildContract(),
                JournalId = journalId,
                MemoryInput = text.Trim(),
                AllowTools = allowTools
            });
        }

        private static bool EnqueueTurn(VoiceTurn turn)
        {
            LiveVoiceInput droppedStream = null;
            lock (Gate)
            {
                if (Pending.Count >= MaxQueuedTurns)
                {
                    VoiceTurn dropped = Pending.Dequeue();
                    ResponseJournal.Discard(dropped.JournalId);
                    droppedStream = dropped.Stream;
                    if (droppedStream != null)
                    {
                        TrackedLiveInputs.Remove(droppedStream.Id);
                        if (_liveInput == droppedStream) _liveInput = null;
                    }
                }
                Pending.Enqueue(turn);
                if (!_workerRunning)
                {
                    _workerRunning = true;
                    _ = RunWorkerAsync();
                }
            }
            CancelLiveInput(droppedStream);
            droppedStream?.CommitCancellation.Dispose();
            return true;
        }

        internal static void ResetSession()
        {
            var liveInputs = new List<LiveVoiceInput>();
            lock (Gate)
            {
                while (Pending.Count > 0) ResponseJournal.Discard(Pending.Dequeue().JournalId);
                liveInputs.AddRange(TrackedLiveInputs.Values);
                TrackedLiveInputs.Clear();
                _liveInput = null;
                _processingTurn = false;
                _responseActive = false;
                _responseCancelRequested = false;
            }
            foreach (LiveVoiceInput live in liveInputs) CancelLiveInput(live);
            MainThread.Enqueue(BuddyNetworkAudio.StopPlayback);
            try { _sessionCancel?.Cancel(); } catch { }
            try { _socket?.Abort(); } catch { }
            _socket = null;
            _appliedSessionConfig = null;
        }

        internal static void BeginPushToTalk()
        {
            MainThread.Enqueue(BuddyNetworkAudio.StopPlayback);
            bool cancelActiveResponse;
            var droppedStreams = new List<LiveVoiceInput>();
            lock (Gate)
            {
                while (Pending.Count > 0)
                {
                    VoiceTurn dropped = Pending.Dequeue();
                    ResponseJournal.Discard(dropped.JournalId);
                    if (dropped.Stream != null)
                    {
                        droppedStreams.Add(dropped.Stream);
                        TrackedLiveInputs.Remove(dropped.Stream.Id);
                        if (_liveInput == dropped.Stream) _liveInput = null;
                    }
                }
                cancelActiveResponse = _responseActive;
                if (cancelActiveResponse) _responseCancelRequested = true;
            }
            foreach (LiveVoiceInput dropped in droppedStreams)
            {
                CancelLiveInput(dropped);
                dropped.CommitCancellation.Dispose();
            }
            if (cancelActiveResponse && _socket != null && _socket.State == WebSocketState.Open)
                _ = TrySendCancelAsync();
        }

        private static async Task TrySendCancelAsync()
        {
            // The flag check runs while the send lock is held, serializing the cancel against the
            // next response.create: a turn cannot be created mid-cancel. If the just-cancelled
            // response finished before this task was scheduled the flag has been reset and the
            // newer turn must not be cancelled. Never clear the input audio buffer from here
            // either: the following live turn streams audio into that buffer, so a stale cancel
            // must not wipe it. Uncommitted audio from an aborted turn is cleared by that turn's
            // own abort path.
            try
            {
                await SendLock.WaitAsync(_sessionCancel.Token).ConfigureAwait(false);
                try
                {
                    lock (Gate) if (!_responseCancelRequested) return;
                    byte[] cancel = Encoding.UTF8.GetBytes("{\"type\":\"response.cancel\"}");
                    await _socket.SendAsync(new ArraySegment<byte>(cancel), WebSocketMessageType.Text, true, _sessionCancel.Token)
                        .ConfigureAwait(false);
                }
                finally { SendLock.Release(); }
            }
            catch (Exception ex)
            {
                // The response can finish in the few milliseconds between the state check and
                // response.cancel. That is expected and must never tear down the voice session.
                Plugin.Log?.LogDebug("Realtime cancellation skipped: " + ex.Message);
            }
        }

        private static async Task RunWorkerAsync()
        {
            try
            {
                while (true)
                {
                    VoiceTurn turn;
                    lock (Gate)
                    {
                        if (Pending.Count == 0) break;
                        turn = Pending.Dequeue();
                        _processingTurn = true;
                    }
                    try { await ProcessTurnAsync(turn).ConfigureAwait(false); }
                    catch (Exception ex)
                    {
                        if (turn.Stream != null)
                        {
                            lock (Gate)
                                if (_liveInput == turn.Stream) _liveInput = null;
                            CancelLiveInput(turn.Stream);
                        }
                        ResponseJournal.Discard(turn.JournalId);
                        turn.JournalId = 0;
                        Plugin.Log?.LogWarning("Realtime voice turn failed: " + ex.GetType().Name + ": " + ex.Message);
                        CloseSocket();
                        string reason = ex.Message ?? "Unknown Realtime error";
                        reason = reason.Replace('\r', ' ').Replace('\n', ' ').Trim();
                        if (reason.Length > 140) reason = reason.Substring(0, 140) + "...";
                        QueueHint("Realtime error: " + reason);
                    }
                    finally
                    {
                        lock (Gate)
                        {
                            _processingTurn = false;
                            if (turn.Stream != null)
                            {
                                TrackedLiveInputs.Remove(turn.Stream.Id);
                                turn.Stream.CommitCancellation.Dispose();
                            }
                        }
                    }
                }
            }
            finally
            {
                lock (Gate)
                {
                    _processingTurn = false;
                    _workerRunning = false;
                    if (Pending.Count > 0)
                    {
                        _workerRunning = true;
                        _ = RunWorkerAsync();
                    }
                }
            }
        }

        private static async Task ProcessTurnAsync(VoiceTurn turn)
        {
            string toolResult = null;
            string inputTranscript = turn.MemoryInput;
            bool expectAudio = Plugin.TtsEnabled?.Value == true;
            await EnsureConnectedAsync().ConfigureAwait(false);
            await EnsureSessionConfigAsync(turn.Contract).ConfigureAwait(false);

            // A live voice turn sends its context immediately before commit. The microphone bytes
            // can already be streaming into OpenAI while the player talks, but an aborted/silent PTT
            // never leaves an orphaned TURN CONTEXT item in the conversation.
            if (turn.Stream == null && !string.IsNullOrWhiteSpace(turn.TurnContext))
                await SendTurnContextAsync(turn.TurnContext).ConfigureAwait(false);

            if (turn.Stream != null)
            {
                await SendAsync("{\"type\":\"input_audio_buffer.clear\"}", _sessionCancel.Token).ConfigureAwait(false);
                while (true)
                {
                    byte[] chunk = null;
                    bool ended;
                    bool cancelled;
                    lock (turn.Stream.Gate)
                    {
                        if (turn.Stream.Chunks.Count > 0)
                        {
                            chunk = turn.Stream.Chunks.Dequeue();
                            turn.Stream.BufferedBytes -= chunk.Length;
                        }
                        ended = turn.Stream.Ended;
                        cancelled = turn.Stream.Cancelled;
                    }

                    if (chunk != null)
                    {
                        string liveAudio = Convert.ToBase64String(chunk);
                        await SendAsync("{\"type\":\"input_audio_buffer.append\",\"audio\":\"" + liveAudio + "\"}", _sessionCancel.Token)
                            .ConfigureAwait(false);
                        continue;
                    }
                    if (cancelled)
                    {
                        await SendAsync("{\"type\":\"input_audio_buffer.clear\"}", _sessionCancel.Token).ConfigureAwait(false);
                        return;
                    }
                    if (ended) break;
                    await turn.Stream.Signal.WaitAsync(_sessionCancel.Token).ConfigureAwait(false);
                }

                if (IsLiveInputCancelled(turn.Stream))
                {
                    await SendAsync("{\"type\":\"input_audio_buffer.clear\"}", _sessionCancel.Token).ConfigureAwait(false);
                    return;
                }
                await SendTurnContextAsync(turn.TurnContext).ConfigureAwait(false);
                if (IsLiveInputCancelled(turn.Stream))
                {
                    await SendAsync("{\"type\":\"input_audio_buffer.clear\"}", _sessionCancel.Token).ConfigureAwait(false);
                    return;
                }
                using (var commitToken = CancellationTokenSource.CreateLinkedTokenSource(
                    _sessionCancel.Token, turn.Stream.CommitCancellation.Token))
                {
                    try
                    {
                        await SendAsync("{\"type\":\"input_audio_buffer.commit\"}", commitToken.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (turn.Stream.CommitCancellation.IsCancellationRequested)
                    {
                        await SendAsync("{\"type\":\"input_audio_buffer.clear\"}", _sessionCancel.Token).ConfigureAwait(false);
                        return;
                    }
                }
                if (turn.JournalId == 0)
                {
                    turn.JournalId = ResponseJournal.NoteInput("voice", turn.PlayerName,
                        "[audio streamed directly to gpt-realtime-2.1-mini while push-to-talk was held]");
                    ResponseJournal.RecordContext(turn.JournalId, turn.TurnContext);
                }
                QueueSpeakerNote(turn.PlayerId, turn.PlayerName);
                double releaseToCommitMs = turn.Stream.ReleasedUtc == default(DateTime)
                    ? -1d
                    : (DateTime.UtcNow - turn.Stream.ReleasedUtc).TotalMilliseconds;
                Plugin.Log?.LogInfo($"Realtime live input committed releaseToCommitMs={releaseToCommitMs:F0}.");
            }
            else if (turn.Pcm24k != null)
            {
                await SendAsync("{\"type\":\"input_audio_buffer.clear\"}", _sessionCancel.Token).ConfigureAwait(false);
                const int chunkBytes = 24000; // 500 ms of mono PCM16 at 24 kHz
                for (int offset = 0; offset < turn.Pcm24k.Length; offset += chunkBytes)
                {
                    int length = Math.Min(chunkBytes, turn.Pcm24k.Length - offset);
                    string audio = Convert.ToBase64String(turn.Pcm24k, offset, length);
                    await SendAsync("{\"type\":\"input_audio_buffer.append\",\"audio\":\"" + audio + "\"}", _sessionCancel.Token)
                        .ConfigureAwait(false);
                }
                await SendAsync("{\"type\":\"input_audio_buffer.commit\"}", _sessionCancel.Token).ConfigureAwait(false);
                if (turn.JournalId == 0)
                {
                    turn.JournalId = ResponseJournal.NoteInput("voice", turn.PlayerName,
                        "[audio processed directly by gpt-realtime-2.1-mini; no separate transcript model]");
                    // The contract is now snapshotted once per session, so the journal needs the
                    // per-turn half to stay traceable back to what Buddy could actually see.
                    ResponseJournal.RecordContext(turn.JournalId, turn.TurnContext);
                }
                QueueSpeakerNote(turn.PlayerId, turn.PlayerName);
            }
            else
            {
                string content = "[{\"type\":\"input_text\",\"text\":\"" + LlmClient.Escape(turn.Text) + "\"}]";
                string item = "{\"type\":\"conversation.item.create\",\"item\":{\"type\":\"message\",\"role\":\"user\",\"content\":" + content + "}}";
                await SendAsync(item, _sessionCancel.Token).ConfigureAwait(false);
                ResponseJournal.RecordContext(turn.JournalId, turn.TurnContext);
            }
            if (!turn.AllowTools)
            {
                // Tool definitions stay in the cached session config; only this response opts out.
                await CreateResponseAsync("{\"type\":\"response.create\",\"response\":{\"tool_choice\":\"none\"}}")
                    .ConfigureAwait(false);
            }
            else
            {
                await CreateResponseAsync().ConfigureAwait(false);
            }

            using (var audio = new MemoryStream())
            {
                // Chunk size is a straight trade between how soon Buddy starts talking and how far
                // ahead playback stays buffered, so it ramps: ship the opening ~100 ms the moment
                // it exists, then switch to ~400 ms so the rest of the line arrives well ahead of
                // the audio thread and never has to be padded with silence.
                const int firstChunkBytes = OutputRate / 5;
                const int streamChunkBytes = OutputRate * 4 / 5;
                int playbackChunkBytes = firstChunkBytes;
                bool queuedAnyAudio = false;
                bool streamAudio = !turn.AllowTools;
                bool streamedAudio = false;
                // Speaking and acting are kept strictly separate: on any turn that may call a tool,
                // no audio reaches the speaker until the response is finished and known to be a
                // plain message. 4.3.0 streamed the moment Realtime labelled an output item a
                // "message", which is true of a preamble the model emits *before* a function call
                // in the same response - so players heard the preamble, then heard a second line
                // after the tool result. Buffering the whole first pass makes one turn produce
                // exactly one spoken line. Realtime generates well ahead of real time, so a short
                // reply costs a fraction of a second of extra latency for that guarantee.
                var completedAudioChunks = new List<byte[]>();
                string assistantTranscript = "";
                var pendingToolCalls = new List<PendingToolCall>();
                int toolCalls = 0;
                int toolResponseRounds = 0;
                bool responseComplete = false;
                byte[] receive = new byte[32768];
                // Bounded by silence, not by wall clock: a response that is still streaming audio
                // must never be cut off mid-sentence, but a socket that stops talking must not hang.
                while (_socket.State == WebSocketState.Open)
                {
                    string message = await ReceiveMessageAsync(receive, IdleTimeoutSeconds).ConfigureAwait(false);
                    if (message == null) throw new IOException("Realtime socket closed.");
                    string type = ReadJsonString(message, "type");
                    if (type == "response.output_item.added")
                    {
                        int item = message.IndexOf("\"item\"", StringComparison.Ordinal);
                        string itemType = item >= 0 ? ReadJsonString(message, "type", item) : null;
                        // Only a turn that cannot call a tool is allowed to start playback early;
                        // see the streamAudio note above for why a "message" item is not proof that
                        // no function call follows it in the same response.
                        if (!turn.AllowTools)
                            streamAudio = string.Equals(itemType, "message", StringComparison.Ordinal);
                    }
                    else if (type == "response.output_audio.delta")
                    {
                        if (turn.Stream != null && !turn.Stream.FirstAudioLogged)
                        {
                            turn.Stream.FirstAudioLogged = true;
                            double releaseToFirstAudioMs = turn.Stream.ReleasedUtc == default(DateTime)
                                ? -1d
                                : (DateTime.UtcNow - turn.Stream.ReleasedUtc).TotalMilliseconds;
                            Plugin.Log?.LogInfo($"Realtime first audio releaseToDeltaMs={releaseToFirstAudioMs:F0}.");
                        }
                        string delta = ReadJsonString(message, "delta");
                        if (!string.IsNullOrEmpty(delta))
                        {
                            byte[] bytes = Convert.FromBase64String(delta);
                            audio.Write(bytes, 0, bytes.Length);
                            if (audio.Length >= playbackChunkBytes)
                            {
                                if (streamAudio)
                                {
                                    QueueAudioChunk(audio.ToArray());
                                    queuedAnyAudio = true;
                                    streamedAudio = true;
                                }
                                else
                                {
                                    completedAudioChunks.Add(audio.ToArray());
                                }
                                audio.SetLength(0);
                                audio.Position = 0;
                                playbackChunkBytes = streamChunkBytes;
                            }
                        }
                    }
                    else if (type == "response.output_audio_transcript.done" || type == "response.output_text.done")
                    {
                        assistantTranscript = ReadJsonString(message, type.Contains("audio") ? "transcript" : "text") ?? assistantTranscript;
                    }
                    else if (type == "response.function_call_arguments.done")
                    {
                        string name = ReadJsonString(message, "name");
                        string callId = ReadJsonString(message, "call_id");
                        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(callId))
                            throw new InvalidOperationException("Realtime returned a function call without a name or call ID.");
                        if (pendingToolCalls.Exists(call => string.Equals(call.CallId, callId, StringComparison.Ordinal)))
                            throw new InvalidOperationException("Realtime returned a duplicate function call ID.");
                        pendingToolCalls.Add(new PendingToolCall
                        {
                            Name = name,
                            CallId = callId,
                            Arguments = ReadJsonString(message, "arguments") ?? "{}"
                        });
                    }
                    else if (type == "response.done")
                    {
                        bool wasCancelled = FinishResponse();
                        if (wasCancelled)
                        {
                            ResponseJournal.Discard(turn.JournalId);
                            turn.JournalId = 0;
                            return;
                        }
                        if (pendingToolCalls.Count > 0)
                        {
                            if (++toolResponseRounds > 6)
                                throw new InvalidOperationException("Realtime tool-response round limit reached for one turn.");

                            // Never play or display a model preamble emitted before the real action result.
                            audio.SetLength(0);
                            audio.Position = 0;
                            completedAudioChunks.Clear();
                            assistantTranscript = "";
                            streamAudio = false;
                            streamedAudio = false;
                            queuedAnyAudio = false;
                            playbackChunkBytes = firstChunkBytes;

                            foreach (PendingToolCall call in pendingToolCalls)
                            {
                                string result;
                                if (toolCalls >= 6)
                                {
                                    result = "failed: per_turn_tool_call_limit_reached";
                                }
                                else
                                {
                                    toolCalls++;
                                    result = await ExecuteRealtimeToolAsync(
                                        call.Name, call.Arguments, turn.PlayerId).ConfigureAwait(false);
                                }
                                toolResult = string.IsNullOrWhiteSpace(toolResult)
                                    ? call.Name + ": " + result
                                    : toolResult + " | " + call.Name + ": " + result;

                                // Every call ID receives an output, including bounded rejections,
                                // so a multi-call response cannot strand an earlier function call.
                                string output = "{\"private_status\":\"" + LlmClient.Escape(result) +
                                    "\",\"note\":\"Status data. Never read aloud or paraphrase. Answer in your own words.\"}";
                                string item = "{\"type\":\"conversation.item.create\",\"item\":{\"type\":\"function_call_output\",\"call_id\":\"" +
                                    LlmClient.Escape(call.CallId) + "\",\"output\":\"" + LlmClient.Escape(output) + "\"}}";
                                await SendAsync(item, _sessionCancel.Token).ConfigureAwait(false);
                            }
                            pendingToolCalls.Clear();
                            // Ask for speech explicitly. Left to its own devices after a function
                            // result the model sometimes returned a text-only response, which the
                            // audio guard below then treated as a failed turn - the player asked
                            // for something, the action happened, and Buddy said nothing at all.
                            await CreateResponseAsync(
                                expectAudio
                                    ? "{\"type\":\"response.create\",\"response\":{\"output_modalities\":[\"audio\"]}}"
                                    : "{\"type\":\"response.create\",\"response\":{\"output_modalities\":[\"text\"]}}")
                                .ConfigureAwait(false);
                            continue;
                        }
                        responseComplete = true;
                        break;
                    }
                    else if (type == "error")
                    {
                        string apiError = ReadNestedErrorMessage(message) ?? message;
                        // Cancellation is deliberately best-effort. If a just-finished response
                        // wins the race, the service emits this error event; ignore it and let the
                        // outstanding turn finish normally instead of disconnecting Buddy.
                        if (IsNoActiveResponseCancellation(apiError) && IsCancellationRequested()) continue;
                        throw new InvalidOperationException(apiError);
                    }
                }

                // Leaving the loop without response.done means the socket went away mid-response.
                // Clearing the flag here stops the next turn from being rejected for an "active
                // response" that no longer exists.
                if (!responseComplete)
                {
                    FinishResponse();
                    throw new IOException("Realtime response ended before completion.");
                }

                if (streamedAudio)
                {
                    byte[] tail = audio.ToArray();
                    if (tail.Length > 0)
                    {
                        QueueAudioChunk(tail);
                        queuedAnyAudio = true;
                    }
                }
                else
                {
                    byte[] pcm;
                    using (var complete = new MemoryStream())
                    {
                        foreach (byte[] chunk in completedAudioChunks)
                            complete.Write(chunk, 0, chunk.Length);
                        byte[] tail = audio.ToArray();
                        if (tail.Length > 0) complete.Write(tail, 0, tail.Length);
                        pcm = complete.ToArray();
                    }
                    if (pcm.Length > 1)
                    {
                        QueueAudioChunk(pcm);
                        queuedAnyAudio = true;
                    }
                }
                if (!string.IsNullOrWhiteSpace(assistantTranscript))
                {
                    QueueConversationMemory(turn.PlayerName, inputTranscript, assistantTranscript);
                    QueueAssistantChat(assistantTranscript, turn.JournalId, toolResult);
                    turn.JournalId = 0;
                }
                if (expectAudio && !queuedAnyAudio)
                {
                    // A response that produced words but no speech is a degraded turn, not a broken
                    // one. Throwing here discarded the whole turn - including an action the game had
                    // already performed - and surfaced as Buddy going silent after doing what he was
                    // asked. Keep the text, log it, and only fail when there is genuinely nothing.
                    if (string.IsNullOrWhiteSpace(assistantTranscript))
                        throw new InvalidOperationException("Realtime response completed without audio or text.");
                    Plugin.Log?.LogWarning(
                        "Realtime response returned text without audio; delivered as chat only: " + assistantTranscript);
                }
                if (!expectAudio && string.IsNullOrWhiteSpace(assistantTranscript))
                    throw new InvalidOperationException("Realtime response completed without text.");
            }
            ResponseJournal.Discard(turn.JournalId);
            turn.JournalId = 0;
        }

        private static async Task SendTurnContextAsync(string turnContext)
        {
            if (string.IsNullOrWhiteSpace(turnContext)) return;
            string context = "{\"type\":\"conversation.item.create\",\"item\":{\"type\":\"message\"," +
                "\"role\":\"system\",\"content\":[{\"type\":\"input_text\",\"text\":\"" +
                LlmClient.Escape(turnContext) + "\"}]}}";
            await SendAsync(context, _sessionCancel.Token).ConfigureAwait(false);
        }

        private static async Task EnsureConnectedAsync()
        {
            if (_socket != null && _socket.State == WebSocketState.Open &&
                _sessionCancel != null && !_sessionCancel.IsCancellationRequested) return;
            CloseSocket();
            _appliedSessionConfig = null;
            _sessionCancel = new CancellationTokenSource();
            _sessionCancel.CancelAfter(TimeSpan.FromMinutes(55));
            _socket = new ClientWebSocket();
            _socket.Options.SetRequestHeader("Authorization", "Bearer " + OpenAiSecrets.CurrentKey);
            const string model = BuddyAiArchitecture.OpenAiRealtimeModel;
            await _socket.ConnectAsync(new Uri("wss://api.openai.com/v1/realtime?model=" + Uri.EscapeDataString(model)), _sessionCancel.Token)
                .ConfigureAwait(false);
            await WaitForEventAsync("session.created", 10).ConfigureAwait(false);
            Plugin.Log?.LogInfo("OpenAI native realtime voice session connected: " + model);
        }

        private static async Task CreateResponseAsync(string request = "{\"type\":\"response.create\"}")
        {
            lock (Gate)
            {
                _responseActive = true;
                _responseCancelRequested = false;
            }
            try { await SendAsync(request, _sessionCancel.Token).ConfigureAwait(false); }
            catch
            {
                FinishResponse();
                throw;
            }
        }

        private static bool FinishResponse()
        {
            lock (Gate)
            {
                bool wasCancelled = _responseCancelRequested;
                _responseActive = false;
                _responseCancelRequested = false;
                return wasCancelled;
            }
        }

        private static bool IsCancellationRequested()
        {
            lock (Gate) return _responseCancelRequested;
        }

        private static bool IsNoActiveResponseCancellation(string message) =>
            !string.IsNullOrEmpty(message) &&
            message.IndexOf("Cancellation failed: no active response", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>
        /// Pushes the session configuration only when it actually differs from what the socket is
        /// already running. Instructions and tool definitions are the cached prefix of every
        /// request, so re-sending an identical blob per turn would throw away the prompt cache and
        /// add a needless round trip before Buddy can start answering.
        /// </summary>
        private static async Task EnsureSessionConfigAsync(string contract)
        {
            string update = BuildSessionUpdate(contract);
            if (string.Equals(update, _appliedSessionConfig, StringComparison.Ordinal)) return;
            await SendAsync(update, _sessionCancel.Token).ConfigureAwait(false);
            await WaitForEventAsync("session.updated", 10).ConfigureAwait(false);
            _appliedSessionConfig = update;
        }

        /// <summary>Built from a contract captured on the main thread when the turn was queued.</summary>
        private static string BuildSessionUpdate(string contract)
        {
            bool spokenOutput = Plugin.TtsEnabled?.Value == true;
            return "{\"type\":\"session.update\",\"session\":{" +
                   "\"type\":\"realtime\",\"model\":\"" + BuddyAiArchitecture.OpenAiRealtimeModel + "\"," +
                   "\"output_modalities\":[\"" + (spokenOutput ? "audio" : "text") + "\"]," +
                   "\"instructions\":\"" + LlmClient.Escape(contract) + "\"," +
                   "\"audio\":{\"input\":{\"format\":{\"type\":\"audio/pcm\",\"rate\":24000}," +
                   "\"noise_reduction\":{\"type\":\"far_field\"},\"turn_detection\":null}," +
                   "\"output\":{\"format\":{\"type\":\"audio/pcm\",\"rate\":24000},\"voice\":\"" +
                   BuddyAiArchitecture.SanitizeRealtimeVoice(Plugin.RealtimeVoiceName?.Value) + "\"}}," +
                   "\"reasoning\":{\"effort\":\"" +
                   BuddyAiArchitecture.SanitizeReasoningEffort(Plugin.ReasoningEffort?.Value) + "\"}," +
                   // Reasoning and audio both draw on this budget. The old 384 could end a reply
                   // mid-word; the cap only bounds a runaway response, it is not a per-turn charge.
                   "\"max_output_tokens\":1200," +
                   // Drop a fifth of the window whenever the conversation overflows instead of
                   // trimming the minimum every turn: one cache miss per truncation, not many.
                   "\"truncation\":{\"type\":\"retention_ratio\",\"retention_ratio\":0.8," +
                   "\"token_limits\":{\"post_instructions\":8000}}," +
                   "\"tool_choice\":\"auto\",\"tools\":[" + ToolDefinitionsJson + "]}}";
        }

        private const string ToolDefinitionsJson =
            "{\"type\":\"function\",\"name\":\"move_buddy\",\"description\":\"Move Buddy ONLY when the speaker actually orders it right now: follow, stay, return to ship, fetch scrap, or scout ahead. A polite request (\\\"can you grab that?\\\", \\\"could you follow me?\\\") IS an order - call it. Following includes going with the speaker through a facility entrance or exit: \\\"come inside with me\\\" and \\\"follow me in\\\" call follow. But a question about the job rather than for it is conversation - never call for \\\"ready to get the scrap?\\\", \\\"do you even fetch scrap?\\\" or similar. Also never call for plans, hypotheticals, complaints, negated requests, or reports of an action already taken. fetch_scrap picks the nearest worthwhile loose scrap on its own; pass item_name only when the speaker names a specific item.\",\"parameters\":{\"type\":\"object\",\"properties\":{\"action\":{\"type\":\"string\",\"enum\":[\"follow\",\"stay\",\"return_to_ship\",\"fetch_scrap\",\"scout_ahead\"]},\"distance_metres\":{\"type\":\"number\",\"description\":\"Scout distance, normally 4 to 18 metres.\"},\"bring_to_player\":{\"type\":\"boolean\",\"description\":\"For fetch_scrap only: deliver to the requesting player instead of the ship.\"},\"item_name\":{\"type\":\"string\",\"description\":\"For fetch_scrap only: the item the speaker named, e.g. 'bolt' or 'propane'. Omit to fetch the nearest worthwhile loose scrap.\"}},\"required\":[\"action\"]}}," +
            "{\"type\":\"function\",\"name\":\"get_ship_status\",\"description\":\"Read current time, credits, quota, deadline, moon, weather, ship scrap, or crew status when the live context does not already answer it.\",\"parameters\":{\"type\":\"object\",\"properties\":{\"topic\":{\"type\":\"string\"}}}}," +
            "{\"type\":\"function\",\"name\":\"list_moons\",\"description\":\"List the moons currently available in this game.\",\"parameters\":{\"type\":\"object\",\"properties\":{}}}," +
            "{\"type\":\"function\",\"name\":\"show_store\",\"description\":\"Read the current store and credit overview.\",\"parameters\":{\"type\":\"object\",\"properties\":{}}}," +
            "{\"type\":\"function\",\"name\":\"route_moon\",\"description\":\"Route the ship to a named moon when the speaker clearly asks to go there.\",\"parameters\":{\"type\":\"object\",\"properties\":{\"moon\":{\"type\":\"string\"}},\"required\":[\"moon\"]}}," +
            "{\"type\":\"function\",\"name\":\"buy_item\",\"description\":\"Buy a named store item only when the speaker explicitly says buy, purchase, order, or refers to the store. Never use this for 'can I have', 'give me', 'get me one', pleading, or begging; genuine begging uses spawn_item and costs no credits. Works in orbit, on the ship and on the moon surface - not from inside the facility. Quantity defaults to one.\",\"parameters\":{\"type\":\"object\",\"properties\":{\"item\":{\"type\":\"string\"},\"quantity\":{\"type\":\"integer\"}},\"required\":[\"item\"]}}," +
            "{\"type\":\"function\",\"name\":\"control_facility_object\",\"description\":\"Enable/open or disable/close a coded facility door, turret, or landmine. The object's number IS its code: 'door D6' means code D6, so pass the speaker's identifier as the code. Only ask for a code when the speaker gave no identifier.\",\"parameters\":{\"type\":\"object\",\"properties\":{\"code\":{\"type\":\"string\"},\"kind\":{\"type\":\"string\",\"enum\":[\"door\",\"turret\",\"landmine\"]},\"enabled\":{\"type\":\"boolean\"}},\"required\":[\"kind\",\"enabled\"]}}," +
            "{\"type\":\"function\",\"name\":\"set_hangar_doors\",\"description\":\"Open or close the ship hangar doors on an explicit request.\",\"parameters\":{\"type\":\"object\",\"properties\":{\"open\":{\"type\":\"boolean\"}},\"required\":[\"open\"]}}," +
            "{\"type\":\"function\",\"name\":\"set_ship_lights\",\"description\":\"Turn the ship room lights on or off on an explicit request.\",\"parameters\":{\"type\":\"object\",\"properties\":{\"on\":{\"type\":\"boolean\"}},\"required\":[\"on\"]}}," +
            "{\"type\":\"function\",\"name\":\"spawn_item\",\"description\":\"Put a normal grabbable item in the current speaker's hands ONLY when they explicitly plead - 'please', 'can I please have', 'I'm begging you'. This takes precedence over buy_item: a plea to have, get, receive, or be given an item is spawning, not purchasing, and costs no credits. Plain requests and demands are refused with one line and no tool call. Enemy and arbitrary prefab spawning is unavailable.\",\"parameters\":{\"type\":\"object\",\"properties\":{\"item\":{\"type\":\"string\"},\"quantity\":{\"type\":\"integer\"}},\"required\":[\"item\"]}}";

        private static async Task<string> ExecuteRealtimeToolAsync(string name, string arguments, int playerId)
        {
            var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var dispatch = new DeferredActionGate();
            MainThread.Enqueue(() =>
            {
                // A socket-thread timeout may cancel this work while it is still waiting in the
                // Unity queue. Once execution has begun, wait for the real result: reporting a
                // failure while a purchase/spawn/control action completes would be a false status.
                if (!dispatch.TryBegin()) return;
                try
                {
                    string result = BuddyRealtimeTools.Execute(name, arguments, playerId);
                    Plugin.Log?.LogInfo("Realtime tool " + name + " -> " + result);
                    completion.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    Plugin.Log?.LogWarning("Realtime tool dispatch: " + ex.Message);
                    completion.TrySetResult("Tool failed: the game rejected that action.");
                }
            });
            Task finished = await Task.WhenAny(completion.Task, Task.Delay(3000)).ConfigureAwait(false);
            if (finished == completion.Task)
                return await completion.Task.ConfigureAwait(false);
            if (dispatch.TryCancel())
                return "Tool failed: the game did not answer in time; the action was cancelled.";

            // The main thread claimed the action at the timeout boundary. It may already have
            // changed game state, so only its actual completion can produce a truthful response.
            return await completion.Task.ConfigureAwait(false);
        }

        /// <summary>
        /// Voice push-to-talk is always a direct address. The speaker was resolved from the host
        /// player list when the turn was queued, never from anything the client sent. This runs on
        /// the socket thread, so the bookkeeping is marshalled back to the main thread.
        /// </summary>
        private static void QueueSpeakerNote(int playerId, string playerName)
        {
            MainThread.Enqueue(() =>
            {
                BuddySocialIntelligence.NoteSpeech(playerId, playerName, true);
                BuddyRelationships.NoteAddressing(playerName);
            });
        }

        private static void QueueConversationMemory(string playerName, string input, string reply)
        {
            if (string.IsNullOrWhiteSpace(input) || string.IsNullOrWhiteSpace(reply)) return;
            if (input.StartsWith("[voice input", StringComparison.OrdinalIgnoreCase)) return;
            MainThread.Enqueue(() => BuddyConversationMemory.Remember(playerName, input, reply));
        }

        private static void QueueAudioChunk(byte[] pcm)
        {
            MainThread.Enqueue(() =>
            {
                var primary = CrewmateRegistry.GetPrimary();
                Vector3 pos = primary?.Enemy != null ? primary.Enemy.transform.position : Vector3.zero;
                BuddyNetworkAudio.QueueHostPcm16(pcm, OutputRate, pos + Vector3.up * 1.6f);
            });
        }

        private static void QueueAssistantChat(string transcript, long journalId, string toolResult) =>
            MainThread.Enqueue(() => QueueAssistantChatNow(transcript, journalId, toolResult));

        private static void QueueAssistantChatNow(string transcript, long journalId, string toolResult)
        {
            var primary = CrewmateRegistry.GetPrimary();
            Vector3 pos = primary?.Enemy != null ? primary.Enemy.transform.position : Vector3.zero;
            ulong netId = primary?.NetworkObjectId ?? 0;
            string name = Plugin.CrewmateName?.Value ?? "Buddy";
            NetMessenger.BroadcastCrewmateChat(name, transcript, pos, netId);
            ProximityChat.TryShowLocal(name, transcript, pos);
            LlmClient.NoteBuddyLine();
            ResponseJournal.RecordReply(journalId, transcript, toolResult);
        }

        private static void QueueHint(string message) => MainThread.Enqueue(() =>
        {
            try { HUDManager.Instance?.DisplayTip("Buddy", message, false, false, "BuddyRealtimeTip"); }
            catch { }
        });

        private static async Task SendAsync(string json, CancellationToken token)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            await SendLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await _socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token).ConfigureAwait(false);
            }
            finally { SendLock.Release(); }
        }

        private static async Task WaitForEventAsync(string expected, int timeoutSeconds)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_sessionCancel.Token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
                byte[] buffer = new byte[32768];
                while (true)
                {
                    string message = await ReadMessageAsync(buffer, timeout.Token).ConfigureAwait(false);
                    if (message == null) throw new IOException("Realtime socket closed.");
                    string type = ReadJsonString(message, "type");
                    if (type == expected) return;
                    if (type == "error") throw new InvalidOperationException(ReadNestedErrorMessage(message) ?? message);
                }
            }
        }

        /// <summary>
        /// Reads one event, failing only if the service goes quiet for <paramref name="idleSeconds"/>.
        /// The timeout is per message rather than per response so a long answer keeps streaming.
        /// </summary>
        private static async Task<string> ReceiveMessageAsync(byte[] buffer, int idleSeconds)
        {
            using (var idle = CancellationTokenSource.CreateLinkedTokenSource(_sessionCancel.Token))
            {
                idle.CancelAfter(TimeSpan.FromSeconds(idleSeconds));
                try
                {
                    return await ReadMessageAsync(buffer, idle.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!_sessionCancel.IsCancellationRequested)
                {
                    throw new TimeoutException("Realtime stopped responding after " + idleSeconds + "s.");
                }
            }
        }

        private static async Task<string> ReadMessageAsync(byte[] buffer, CancellationToken token)
        {
            using (var stream = new MemoryStream())
            {
                WebSocketReceiveResult received;
                do
                {
                    received = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);
                    if (received.MessageType == WebSocketMessageType.Close) return null;
                    stream.Write(buffer, 0, received.Count);
                } while (!received.EndOfMessage);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        private static void CloseSocket()
        {
            lock (Gate)
            {
                _responseActive = false;
                _responseCancelRequested = false;
            }
            try { _sessionCancel?.Cancel(); } catch { }
            try { _socket?.Abort(); _socket?.Dispose(); } catch { }
            _socket = null;
            _sessionCancel = null;
        }

        private static bool TryConvertPcm16kToPcm24k(byte[] input, out byte[] output)
        {
            output = null;
            if (input == null || input.Length < 4 || (input.Length & 1) != 0) return false;
            int sourceSamples = input.Length / 2;
            // StreamingMicCapture keeps chunks on even 16 kHz sample boundaries. Trim an odd tail
            // defensively so every chunk maps exactly through the 3:2 16 kHz -> 24 kHz ratio.
            sourceSamples &= ~1;
            if (sourceSamples < 2) return false;
            int targetSamples = sourceSamples * OutputRate / InputWireRate;
            output = new byte[targetSamples * 2];
            for (int i = 0; i < targetSamples; i++)
            {
                double sourcePos = i * (double)InputWireRate / OutputRate;
                int a = Math.Min(sourceSamples - 1, (int)sourcePos);
                int b = Math.Min(sourceSamples - 1, a + 1);
                double fraction = sourcePos - a;
                short sa = BitConverter.ToInt16(input, a * 2);
                short sb = BitConverter.ToInt16(input, b * 2);
                short sample = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, sa + (sb - sa) * fraction));
                output[i * 2] = (byte)(sample & 0xff);
                output[i * 2 + 1] = (byte)((sample >> 8) & 0xff);
            }
            return true;
        }

        private static bool TryConvertWavToPcm24k(byte[] wav, out byte[] output)
        {
            output = null;
            if (wav == null || wav.Length < 44 || Encoding.ASCII.GetString(wav, 0, 4) != "RIFF") return false;
            int rate = BitConverter.ToInt32(wav, 24);
            short channels = BitConverter.ToInt16(wav, 22);
            short bits = BitConverter.ToInt16(wav, 34);
            if (rate < 8000 || channels != 1 || bits != 16) return false;
            int data = -1, length = 0;
            for (int at = 12; at + 8 <= wav.Length;)
            {
                string id = Encoding.ASCII.GetString(wav, at, 4);
                int size = BitConverter.ToInt32(wav, at + 4);
                int payloadOffset = at + 8;
                if (size < 0 || size > wav.Length - payloadOffset) return false;
                if (id == "data") { data = at + 8; length = size; break; }
                at += 8 + size + (size & 1);
            }
            if (data < 0 || length < 2) return false;
            int sourceSamples = length / 2;
            int targetSamples = (int)Math.Ceiling(sourceSamples * (double)OutputRate / rate);
            output = new byte[targetSamples * 2];
            for (int i = 0; i < targetSamples; i++)
            {
                double sourcePos = i * (double)rate / OutputRate;
                int a = Math.Min(sourceSamples - 1, (int)sourcePos);
                int b = Math.Min(sourceSamples - 1, a + 1);
                double fraction = sourcePos - a;
                short sa = BitConverter.ToInt16(wav, data + a * 2);
                short sb = BitConverter.ToInt16(wav, data + b * 2);
                short sample = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, sa + (sb - sa) * fraction));
                output[i * 2] = (byte)(sample & 0xff);
                output[i * 2 + 1] = (byte)((sample >> 8) & 0xff);
            }
            return true;
        }

        private static string ReadNestedErrorMessage(string json)
        {
            int error = json.IndexOf("\"error\"", StringComparison.Ordinal);
            return ReadJsonString(json, "message", error < 0 ? 0 : error);
        }

        private static string ReadJsonString(string json, string key) => ReadJsonString(json, key, 0);

        private static string ReadJsonString(string json, string key, int start)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key)) return null;
            int keyIndex = json.IndexOf("\"" + key + "\"", Math.Max(0, start), StringComparison.Ordinal);
            if (keyIndex < 0) return null;
            int colon = json.IndexOf(':', keyIndex + key.Length + 2);
            if (colon < 0) return null;
            int i = colon + 1;
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            if (i >= json.Length || json[i++] != '"') return null;
            var sb = new StringBuilder();
            while (i < json.Length)
            {
                char c = json[i++];
                if (c == '"') break;
                if (c != '\\' || i >= json.Length) { sb.Append(c); continue; }
                char n = json[i++];
                switch (n)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 3 < json.Length && int.TryParse(json.Substring(i, 4), System.Globalization.NumberStyles.HexNumber, null, out int code))
                        { sb.Append((char)code); i += 4; }
                        break;
                    default: sb.Append(n); break;
                }
            }
            return sb.ToString();
        }
    }
}
