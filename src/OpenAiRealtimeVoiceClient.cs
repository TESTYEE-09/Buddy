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
        private const int MaxQueuedTurns = 3;
        private static readonly ConcurrentQueue<Action> MainThread = new ConcurrentQueue<Action>();
        private static readonly Queue<VoiceTurn> Pending = new Queue<VoiceTurn>();
        private static readonly object Gate = new object();
        private static bool _workerRunning;
        // A response only exists after response.create has been sent and before response.done.
        // Keep this separate from the websocket state: pressing PTT between turns must not send
        // response.cancel, because the Realtime API correctly rejects that with "no active response".
        private static bool _responseActive;
        private static bool _responseCancelRequested;
        private static ClientWebSocket _socket;
        private static CancellationTokenSource _sessionCancel;
        private static readonly SemaphoreSlim SendLock = new SemaphoreSlim(1, 1);

        private sealed class VoiceTurn
        {
            public byte[] Pcm24k;
            public string Text;
            public string ImageJpegBase64;
            public int PlayerId;
            public string PlayerName;
            public string Instructions;
            public long JournalId;
            public bool SuppressChat;
            public bool AllowTools;
            public string MemoryInput;
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

        internal static bool EnqueueWav(byte[] wav, int playerId, string playerName)
        {
            if (!Enabled || wav == null || !TryConvertWavToPcm24k(wav, out byte[] pcm)) return false;
            string safeName = PromptSafety.SanitizePlayerName(playerName);
            string livePrompt = BuddyConversationPrompt.Build() +
                "\n\nCURRENT VOICE TURN\nSpeaker: " + safeName +
                ".\n" + GameSensors.BuildLiveContext(playerId);
            lock (Gate)
            {
                if (Pending.Count >= MaxQueuedTurns)
                    ResponseJournal.Discard(Pending.Dequeue().JournalId);
                Pending.Enqueue(new VoiceTurn
                {
                    Pcm24k = pcm,
                    PlayerId = playerId,
                    PlayerName = safeName,
                    Instructions = livePrompt,
                    AllowTools = true,
                    MemoryInput = "[voice input understood directly by gpt-realtime-2.1-mini]"
                });
                if (!_workerRunning)
                {
                    _workerRunning = true;
                    _ = RunWorkerAsync();
                }
            }
            return true;
        }

        internal static bool EnqueueText(string text, string playerName, int playerId, long journalId, bool includeScreenshot, bool allowTools)
        {
            if (!Enabled || string.IsNullOrWhiteSpace(text)) return false;
            string image = null;
            if (includeScreenshot && Plugin.VisionEnabled?.Value == true)
                VisionCapture.TryCaptureJpegBase64(out image);
            string instructions = BuildTurnInstructions(playerName, playerId);
            return EnqueueTurn(new VoiceTurn
            {
                Text = text.Trim(),
                ImageJpegBase64 = image,
                PlayerId = playerId,
                PlayerName = PromptSafety.SanitizePlayerName(playerName),
                Instructions = instructions,
                JournalId = journalId,
                MemoryInput = LlmClient.BuildHistoryContent(text, false),
                AllowTools = allowTools
            });
        }

        internal static bool EnqueueExactSpeech(string text)
        {
            if (!Enabled || Plugin.TtsEnabled?.Value != true || string.IsNullOrWhiteSpace(text)) return false;
            return EnqueueTurn(new VoiceTurn
            {
                Text = "Read this exact Buddy line aloud without adding, removing, or changing any words: \"" + text.Trim() + "\"",
                PlayerId = -1,
                PlayerName = "Buddy",
                Instructions = BuddyConversationPrompt.Build() +
                    "\nThis is an internal voice-rendering turn. Speak the supplied line exactly. Do not call tools.",
                SuppressChat = true
            });
        }

        private static bool EnqueueTurn(VoiceTurn turn)
        {
            lock (Gate)
            {
                if (Pending.Count >= MaxQueuedTurns)
                    ResponseJournal.Discard(Pending.Dequeue().JournalId);
                Pending.Enqueue(turn);
                if (!_workerRunning)
                {
                    _workerRunning = true;
                    _ = RunWorkerAsync();
                }
            }
            return true;
        }

        private static string BuildTurnInstructions(string playerName, int playerId) =>
            BuddyConversationPrompt.Build() +
            "\n\nCURRENT TURN\nSpeaker: " + PromptSafety.SanitizePlayerName(playerName) + ".\n" +
            GameSensors.BuildLiveContext(playerId);

        internal static void ResetSession()
        {
            lock (Gate)
            {
                while (Pending.Count > 0) ResponseJournal.Discard(Pending.Dequeue().JournalId);
                _responseActive = false;
                _responseCancelRequested = false;
            }
            try { _sessionCancel?.Cancel(); } catch { }
            try { _socket?.Abort(); } catch { }
            _socket = null;
        }

        internal static void BeginPushToTalk()
        {
            MainThread.Enqueue(BuddyNetworkAudio.StopPlayback);
            bool cancelActiveResponse;
            lock (Gate)
            {
                while (Pending.Count > 0) ResponseJournal.Discard(Pending.Dequeue().JournalId);
                cancelActiveResponse = _responseActive;
                if (cancelActiveResponse) _responseCancelRequested = true;
            }
            if (cancelActiveResponse && _socket != null && _socket.State == WebSocketState.Open)
                _ = TrySendCancelAsync();
        }

        private static async Task TrySendCancelAsync()
        {
            try
            {
                await SendAsync("{\"type\":\"response.cancel\"}", _sessionCancel.Token).ConfigureAwait(false);
                await SendAsync("{\"type\":\"input_audio_buffer.clear\"}", _sessionCancel.Token).ConfigureAwait(false);
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
                    }
                    try { await ProcessTurnAsync(turn).ConfigureAwait(false); }
                    catch (Exception ex)
                    {
                        ResponseJournal.Discard(turn.JournalId);
                        turn.JournalId = 0;
                        Plugin.Log?.LogWarning("Realtime voice turn failed: " + ex.GetType().Name + ": " + ex.Message);
                        CloseSocket();
                        string reason = ex.Message ?? "Unknown Realtime error";
                        reason = reason.Replace('\r', ' ').Replace('\n', ' ').Trim();
                        if (reason.Length > 140) reason = reason.Substring(0, 140) + "...";
                        QueueHint("Realtime error: " + reason);
                    }
                }
            }
            finally
            {
                lock (Gate)
                {
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
            string update = BuildSessionUpdate(turn.Instructions, expectAudio, turn.AllowTools);
            await SendAsync(update, _sessionCancel.Token).ConfigureAwait(false);
            await WaitForEventAsync("session.updated", 10).ConfigureAwait(false);

            if (turn.Pcm24k != null)
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
                    turn.JournalId = ResponseJournal.NoteInput("voice", turn.PlayerName,
                        "[audio processed directly by gpt-realtime-2.1-mini; no separate transcript model]");
                QueueSpeakerNote(turn.PlayerId, turn.PlayerName);
            }
            else if (!turn.SuppressChat)
            {
                string content = "[{\"type\":\"input_text\",\"text\":\"" + LlmClient.Escape(turn.Text) + "\"}";
                if (!string.IsNullOrWhiteSpace(turn.ImageJpegBase64))
                    content += ",{\"type\":\"input_image\",\"image_url\":\"data:image/jpeg;base64," + turn.ImageJpegBase64 + "\"}";
                content += "]";
                string item = "{\"type\":\"conversation.item.create\",\"item\":{\"type\":\"message\",\"role\":\"user\",\"content\":" + content + "}}";
                await SendAsync(item, _sessionCancel.Token).ConfigureAwait(false);
            }
            if (turn.SuppressChat)
            {
                string exactInstructions = turn.Instructions + "\n" + turn.Text;
                await CreateResponseAsync("{\"type\":\"response.create\",\"response\":{\"conversation\":\"none\",\"output_modalities\":[\"audio\"],\"instructions\":\"" +
                    LlmClient.Escape(exactInstructions) + "\"}}").ConfigureAwait(false);
            }
            else
            {
                await CreateResponseAsync().ConfigureAwait(false);
            }

            using (var audio = new MemoryStream())
            {
                const int playbackChunkBytes = OutputRate * 2; // roughly one second of PCM16
                bool queuedAnyAudio = false;
                // A Realtime model may emit a friendly preamble before deciding to call a tool.
                // Do not play it yet: only confirmed, final output reaches the crew.
                var completedAudioChunks = new List<byte[]>();
                string assistantTranscript = "";
                string pendingToolName = null;
                string pendingToolCallId = null;
                string pendingToolArguments = null;
                int toolCalls = 0;
                DateTime deadline = DateTime.UtcNow.AddSeconds(35);
                byte[] receive = new byte[32768];
                while (DateTime.UtcNow < deadline && _socket.State == WebSocketState.Open)
                {
                    string message = await ReceiveMessageAsync(receive, _sessionCancel.Token).ConfigureAwait(false);
                    if (message == null) throw new IOException("Realtime socket closed.");
                    string type = ReadJsonString(message, "type");
                    if (type == "response.output_audio.delta")
                    {
                        string delta = ReadJsonString(message, "delta");
                        if (!string.IsNullOrEmpty(delta))
                        {
                            byte[] bytes = Convert.FromBase64String(delta);
                            audio.Write(bytes, 0, bytes.Length);
                            if (audio.Length >= playbackChunkBytes)
                            {
                                completedAudioChunks.Add(audio.ToArray());
                                audio.SetLength(0);
                                audio.Position = 0;
                            }
                        }
                    }
                    else if (type == "response.output_audio_transcript.done" || type == "response.output_text.done")
                    {
                        assistantTranscript = ReadJsonString(message, type.Contains("audio") ? "transcript" : "text") ?? assistantTranscript;
                    }
                    else if (type == "response.function_call_arguments.done")
                    {
                        pendingToolName = ReadJsonString(message, "name");
                        pendingToolCallId = ReadJsonString(message, "call_id");
                        pendingToolArguments = ReadJsonString(message, "arguments") ?? "{}";
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
                        if (!string.IsNullOrWhiteSpace(pendingToolName) && !string.IsNullOrWhiteSpace(pendingToolCallId))
                        {
                            if (++toolCalls > 6) throw new InvalidOperationException("Realtime tool-call limit reached for one turn.");
                            string result = await ExecuteRealtimeToolAsync(
                                pendingToolName, pendingToolArguments, turn.PlayerId).ConfigureAwait(false);
                            toolResult = string.IsNullOrWhiteSpace(toolResult)
                                ? pendingToolName + ": " + result
                                : toolResult + " | " + pendingToolName + ": " + result;

                            // Never play or display a model preamble emitted before the real action result.
                            audio.SetLength(0);
                            audio.Position = 0;
                            completedAudioChunks.Clear();
                            assistantTranscript = "";

                            string output = "{\"result\":\"" + LlmClient.Escape(result) + "\"}";
                            string item = "{\"type\":\"conversation.item.create\",\"item\":{\"type\":\"function_call_output\",\"call_id\":\"" +
                                LlmClient.Escape(pendingToolCallId) + "\",\"output\":\"" + LlmClient.Escape(output) + "\"}}";
                            pendingToolName = null;
                            pendingToolCallId = null;
                            pendingToolArguments = null;
                            await SendAsync(item, _sessionCancel.Token).ConfigureAwait(false);
                            await CreateResponseAsync().ConfigureAwait(false);
                            deadline = DateTime.UtcNow.AddSeconds(35);
                            continue;
                        }
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
                if (!turn.SuppressChat && !string.IsNullOrWhiteSpace(assistantTranscript))
                {
                    QueueConversationMemory(turn.PlayerName, inputTranscript, assistantTranscript);
                    QueueAssistantChat(assistantTranscript, turn.JournalId, toolResult);
                    turn.JournalId = 0;
                }
                if (expectAudio && !queuedAnyAudio) throw new InvalidOperationException("Realtime response completed without audio.");
                if (!expectAudio && !turn.SuppressChat && string.IsNullOrWhiteSpace(assistantTranscript))
                    throw new InvalidOperationException("Realtime response completed without text.");
            }
            ResponseJournal.Discard(turn.JournalId);
            turn.JournalId = 0;
        }

        private static async Task EnsureConnectedAsync()
        {
            if (_socket != null && _socket.State == WebSocketState.Open &&
                _sessionCancel != null && !_sessionCancel.IsCancellationRequested) return;
            CloseSocket();
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

        private static string BuildSessionUpdate(string instructions, bool spokenOutput, bool allowTools)
        {
            string tools = allowTools
                ? "\"tool_choice\":\"auto\",\"tools\":[" + ToolDefinitionsJson + "]"
                : "\"tool_choice\":\"none\",\"tools\":[]";
            return "{\"type\":\"session.update\",\"session\":{" +
                   "\"type\":\"realtime\",\"model\":\"" + BuddyAiArchitecture.OpenAiRealtimeModel + "\"," +
                   "\"output_modalities\":[\"" + (spokenOutput ? "audio" : "text") + "\"]," +
                   "\"instructions\":\"" + LlmClient.Escape(instructions) + "\"," +
                   "\"audio\":{\"input\":{\"format\":{\"type\":\"audio/pcm\",\"rate\":24000}," +
                   "\"noise_reduction\":{\"type\":\"far_field\"},\"turn_detection\":null}," +
                   "\"output\":{\"format\":{\"type\":\"audio/pcm\",\"rate\":24000},\"voice\":\"" +
                   BuddyAiArchitecture.SanitizeRealtimeVoice(Plugin.RealtimeVoiceName?.Value) + "\"}}," +
                   "\"reasoning\":{\"effort\":\"low\"},\"max_output_tokens\":1024," + tools + "}}";
        }

        private const string ToolDefinitionsJson =
            "{\"type\":\"function\",\"name\":\"move_buddy\",\"description\":\"Move Buddy when the current speaker asks him to follow, stay, return to ship, fetch scrap, or scout ahead. Do not call for hypotheticals, complaints, negated requests, or reports of an action already taken.\",\"parameters\":{\"type\":\"object\",\"properties\":{\"action\":{\"type\":\"string\",\"enum\":[\"follow\",\"stay\",\"return_to_ship\",\"fetch_scrap\",\"scout_ahead\"]},\"distance_metres\":{\"type\":\"number\",\"description\":\"Scout distance, normally 4 to 18 metres.\"},\"bring_to_player\":{\"type\":\"boolean\",\"description\":\"For fetch_scrap only: deliver to the requesting player instead of the ship.\"}},\"required\":[\"action\"]}}," +
            "{\"type\":\"function\",\"name\":\"get_ship_status\",\"description\":\"Read current time, credits, quota, deadline, moon, weather, ship scrap, or crew status when the live context does not already answer it.\",\"parameters\":{\"type\":\"object\",\"properties\":{\"topic\":{\"type\":\"string\"}}}}," +
            "{\"type\":\"function\",\"name\":\"list_moons\",\"description\":\"List the moons currently available in this game.\",\"parameters\":{\"type\":\"object\",\"properties\":{}}}," +
            "{\"type\":\"function\",\"name\":\"show_store\",\"description\":\"Read the current store and credit overview.\",\"parameters\":{\"type\":\"object\",\"properties\":{}}}," +
            "{\"type\":\"function\",\"name\":\"route_moon\",\"description\":\"Route the ship to a named moon when the speaker clearly asks to go there.\",\"parameters\":{\"type\":\"object\",\"properties\":{\"moon\":{\"type\":\"string\"}},\"required\":[\"moon\"]}}," +
            "{\"type\":\"function\",\"name\":\"buy_item\",\"description\":\"Buy a named store item when the speaker clearly asks for a purchase. Quantity defaults to one.\",\"parameters\":{\"type\":\"object\",\"properties\":{\"item\":{\"type\":\"string\"},\"quantity\":{\"type\":\"integer\"}},\"required\":[\"item\"]}}," +
            "{\"type\":\"function\",\"name\":\"control_facility_object\",\"description\":\"Enable/open or disable/close a coded facility door, turret, or landmine. Never guess a requested code.\",\"parameters\":{\"type\":\"object\",\"properties\":{\"code\":{\"type\":\"string\"},\"kind\":{\"type\":\"string\",\"enum\":[\"door\",\"turret\",\"landmine\"]},\"enabled\":{\"type\":\"boolean\"}},\"required\":[\"kind\",\"enabled\"]}}," +
            "{\"type\":\"function\",\"name\":\"set_hangar_doors\",\"description\":\"Open or close the ship hangar doors on an explicit request.\",\"parameters\":{\"type\":\"object\",\"properties\":{\"open\":{\"type\":\"boolean\"}},\"required\":[\"open\"]}}," +
            "{\"type\":\"function\",\"name\":\"set_ship_lights\",\"description\":\"Turn the ship room lights on or off on an explicit request.\",\"parameters\":{\"type\":\"object\",\"properties\":{\"on\":{\"type\":\"boolean\"}},\"required\":[\"on\"]}}," +
            "{\"type\":\"function\",\"name\":\"spawn_item\",\"description\":\"Spawn a normal grabbable item in front of the current speaker when they clearly ask Buddy to create it. Enemy and arbitrary prefab spawning is unavailable.\",\"parameters\":{\"type\":\"object\",\"properties\":{\"item\":{\"type\":\"string\"},\"quantity\":{\"type\":\"integer\"}},\"required\":[\"item\"]}}";

        private static async Task<string> ExecuteRealtimeToolAsync(string name, string arguments, int playerId)
        {
            var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            MainThread.Enqueue(() =>
            {
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
            return finished == completion.Task
                ? await completion.Task.ConfigureAwait(false)
                : "Tool failed: the game did not answer in time.";
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
                    string message = await ReceiveMessageAsync(buffer, timeout.Token).ConfigureAwait(false);
                    if (message == null) throw new IOException("Realtime socket closed.");
                    string type = ReadJsonString(message, "type");
                    if (type == expected) return;
                    if (type == "error") throw new InvalidOperationException(ReadNestedErrorMessage(message) ?? message);
                }
            }
        }

        private static async Task<string> ReceiveMessageAsync(byte[] buffer, CancellationToken token)
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
