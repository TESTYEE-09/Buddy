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
    /// <summary>Persistent host-side OpenAI speech-to-speech session for push-to-talk turns.</summary>
    internal static class OpenAiRealtimeVoiceClient
    {
        private const int OutputRate = 24000;
        private const int MaxQueuedTurns = 3;
        private static readonly ConcurrentQueue<Action> MainThread = new ConcurrentQueue<Action>();
        private static readonly Queue<VoiceTurn> Pending = new Queue<VoiceTurn>();
        private static readonly object Gate = new object();
        private static bool _workerRunning;
        private static ClientWebSocket _socket;
        private static CancellationTokenSource _sessionCancel;
        private static readonly SemaphoreSlim SendLock = new SemaphoreSlim(1, 1);

        private sealed class VoiceTurn
        {
            public byte[] Pcm24k;
            public int PlayerId;
            public string PlayerName;
            public string Instructions;
        }

        internal static bool Enabled => GroqSecrets.IsOpenAi &&
            !string.IsNullOrWhiteSpace(Plugin.RealtimeVoiceModel?.Value) &&
            Plugin.RealtimeVoiceModel.Value.StartsWith("gpt-realtime", StringComparison.OrdinalIgnoreCase);

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
            string livePrompt = BuddyConversationPrompt.Build() +
                "\n\nCURRENT VOICE TURN\nSpeaker: " + (playerName ?? "Player") +
                " (player id " + playerId + ").\n" + GameSensors.BuildLiveContext() +
                "\nFor an actual game action or live status query, call execute_game_command with the speaker's exact intent. " +
                "Never claim an action happened until its function output confirms it.";
            lock (Gate)
            {
                if (Pending.Count >= MaxQueuedTurns) Pending.Dequeue();
                Pending.Enqueue(new VoiceTurn
                {
                    Pcm24k = pcm,
                    PlayerId = playerId,
                    PlayerName = playerName ?? "Player",
                    Instructions = livePrompt
                });
                if (!_workerRunning)
                {
                    _workerRunning = true;
                    _ = RunWorkerAsync();
                }
            }
            return true;
        }

        internal static void ResetSession()
        {
            lock (Gate) Pending.Clear();
            try { _sessionCancel?.Cancel(); } catch { }
            try { _socket?.Abort(); } catch { }
            _socket = null;
        }

        internal static void BeginPushToTalk()
        {
            MainThread.Enqueue(BuddyNetworkAudio.StopPlayback);
            lock (Gate) Pending.Clear();
            if (_socket != null && _socket.State == WebSocketState.Open)
                _ = TrySendCancelAsync();
        }

        private static async Task TrySendCancelAsync()
        {
            try
            {
                await SendAsync("{\"type\":\"response.cancel\"}", _sessionCancel.Token).ConfigureAwait(false);
                await SendAsync("{\"type\":\"input_audio_buffer.clear\"}", _sessionCancel.Token).ConfigureAwait(false);
            }
            catch { }
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
                        Plugin.Log?.LogWarning("Realtime voice turn failed: " + ex.GetType().Name + ": " + ex.Message);
                        CloseSocket();
                        QueueHint("Buddy's realtime voice disconnected. Try speaking again.");
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
            await EnsureConnectedAsync().ConfigureAwait(false);
            string update = BuildSessionUpdate(turn.Instructions);
            await SendAsync(update, _sessionCancel.Token).ConfigureAwait(false);
            await WaitForEventAsync("session.updated", 10).ConfigureAwait(false);

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
            await SendAsync("{\"type\":\"response.create\"}", _sessionCancel.Token).ConfigureAwait(false);

            using (var audio = new MemoryStream())
            {
                const int playbackChunkBytes = OutputRate * 2; // roughly one second of PCM16
                bool queuedAnyAudio = false;
                string assistantTranscript = "";
                string pendingCallId = null;
                string pendingCommand = null;
                DateTime deadline = DateTime.UtcNow.AddSeconds(35);
                byte[] receive = new byte[32768];
                while (DateTime.UtcNow < deadline && _socket.State == WebSocketState.Open)
                {
                    string message = await ReceiveMessageAsync(receive, _sessionCancel.Token).ConfigureAwait(false);
                    if (message == null) throw new IOException("Realtime socket closed.");
                    string type = ReadJsonString(message, "type");
                    if (type == "conversation.item.input_audio_transcription.completed")
                    {
                        string transcript = ReadJsonString(message, "transcript");
                        if (!string.IsNullOrWhiteSpace(transcript)) QueueInputTranscript(turn.PlayerName, transcript);
                    }
                    else if (type == "response.output_audio.delta")
                    {
                        string delta = ReadJsonString(message, "delta");
                        if (!string.IsNullOrEmpty(delta))
                        {
                            byte[] bytes = Convert.FromBase64String(delta);
                            audio.Write(bytes, 0, bytes.Length);
                            if (audio.Length >= playbackChunkBytes)
                            {
                                QueueAudioChunk(audio.ToArray());
                                audio.SetLength(0);
                                audio.Position = 0;
                                queuedAnyAudio = true;
                            }
                        }
                    }
                    else if (type == "response.output_audio_transcript.done" || type == "response.output_text.done")
                    {
                        assistantTranscript = ReadJsonString(message, type.Contains("audio") ? "transcript" : "text") ?? assistantTranscript;
                    }
                    else if (type == "response.function_call_arguments.done")
                    {
                        pendingCallId = ReadJsonString(message, "call_id");
                        string arguments = ReadJsonString(message, "arguments");
                        pendingCommand = ReadJsonString(arguments, "command") ?? arguments;
                    }
                    else if (type == "response.done")
                    {
                        if (!string.IsNullOrEmpty(pendingCallId))
                        {
                            audio.SetLength(0);
                            audio.Position = 0;
                            assistantTranscript = "";
                            string result = await ExecuteCommandOnMainThread(turn.PlayerId, pendingCommand).ConfigureAwait(false);
                            string output = "{\"result\":\"" + LlmClient.Escape(result) + "\"}";
                            string toolEvent = "{\"type\":\"conversation.item.create\",\"item\":{\"type\":\"function_call_output\",\"call_id\":\"" +
                                               LlmClient.Escape(pendingCallId) + "\",\"output\":\"" + LlmClient.Escape(output) + "\"}}";
                            await SendAsync(toolEvent, _sessionCancel.Token).ConfigureAwait(false);
                            await SendAsync("{\"type\":\"response.create\"}", _sessionCancel.Token).ConfigureAwait(false);
                            pendingCallId = null;
                            pendingCommand = null;
                            continue;
                        }
                        if (audio.Length > 0 || !string.IsNullOrWhiteSpace(assistantTranscript)) break;
                    }
                    else if (type == "error")
                    {
                        throw new InvalidOperationException(ReadNestedErrorMessage(message) ?? message);
                    }
                }

                byte[] pcm = audio.ToArray();
                if (pcm.Length > 1)
                {
                    QueueAudioChunk(pcm);
                    queuedAnyAudio = true;
                }
                if (!string.IsNullOrWhiteSpace(assistantTranscript)) QueueAssistantChat(assistantTranscript);
                if (!queuedAnyAudio) throw new InvalidOperationException("Realtime response completed without audio.");
            }
        }

        private static async Task EnsureConnectedAsync()
        {
            if (_socket != null && _socket.State == WebSocketState.Open &&
                _sessionCancel != null && !_sessionCancel.IsCancellationRequested) return;
            CloseSocket();
            _sessionCancel = new CancellationTokenSource();
            _sessionCancel.CancelAfter(TimeSpan.FromMinutes(55));
            _socket = new ClientWebSocket();
            _socket.Options.SetRequestHeader("Authorization", "Bearer " + GroqSecrets.CurrentKey);
            string model = Plugin.RealtimeVoiceModel?.Value?.Trim() ?? "gpt-realtime-2.1-mini";
            await _socket.ConnectAsync(new Uri("wss://api.openai.com/v1/realtime?model=" + Uri.EscapeDataString(model)), _sessionCancel.Token)
                .ConfigureAwait(false);
            await WaitForEventAsync("session.created", 10).ConfigureAwait(false);
            Plugin.Log?.LogInfo("OpenAI native realtime voice session connected: " + model);
        }

        private static string BuildSessionUpdate(string instructions)
        {
            return "{\"type\":\"session.update\",\"session\":{" +
                   "\"type\":\"realtime\",\"model\":\"" + LlmClient.Escape(Plugin.RealtimeVoiceModel.Value.Trim()) + "\"," +
                   "\"output_modalities\":[\"audio\"]," +
                   "\"instructions\":\"" + LlmClient.Escape(instructions) + "\"," +
                   "\"audio\":{\"input\":{\"format\":{\"type\":\"audio/pcm\",\"rate\":24000}," +
                   "\"transcription\":{\"model\":\"gpt-realtime-whisper\"},\"turn_detection\":null}," +
                   "\"output\":{\"format\":{\"type\":\"audio/pcm\"},\"voice\":\"ash\"}}," +
                   "\"tool_choice\":\"auto\",\"tools\":[{\"type\":\"function\",\"name\":\"execute_game_command\"," +
                   "\"description\":\"Execute an explicit Lethal Company movement, scouting, scrap, ship, terminal, purchase, facility, status, or polite spawn command. Do not call for ordinary conversation.\"," +
                   "\"parameters\":{\"type\":\"object\",\"properties\":{\"command\":{\"type\":\"string\",\"description\":\"The speaker's exact command including target, quantity, code and politeness.\"}},\"required\":[\"command\"]}}]}}";
        }

        private static async Task<string> ExecuteCommandOnMainThread(int playerId, string command)
        {
            var done = new TaskCompletionSource<string>();
            MainThread.Enqueue(() =>
            {
                try { done.TrySetResult(ChatObserver.ExecuteDeterministicOnly(command, playerId)); }
                catch (Exception ex) { done.TrySetResult("Command failed: " + ex.Message); }
            });
            return await done.Task.ConfigureAwait(false);
        }

        private static void QueueInputTranscript(string playerName, string transcript)
        {
            MainThread.Enqueue(() =>
            {
                Plugin.Log?.LogInfo("Realtime voice transcript " + playerName + ": " + transcript);
                HUDManager.Instance?.AddChatMessage(transcript, playerName + " (voice)");
            });
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

        private static void QueueAssistantChat(string transcript) => MainThread.Enqueue(() => QueueAssistantChatNow(transcript));

        private static void QueueAssistantChatNow(string transcript)
        {
            var primary = CrewmateRegistry.GetPrimary();
            Vector3 pos = primary?.Enemy != null ? primary.Enemy.transform.position : Vector3.zero;
            ulong netId = primary?.NetworkObjectId ?? 0;
            string name = Plugin.CrewmateName?.Value ?? "Buddy";
            NetMessenger.BroadcastCrewmateChat(name, transcript, pos, netId);
            ProximityChat.TryShowLocal(name, transcript, pos);
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
                if (size < 0 || at + 8 + size > wav.Length) return false;
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
