using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using GameNetcodeStuff;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Networking;

namespace LethalAICrewmate
{
    /// <summary>
    /// v1.4.3 multiplayer voice relay.
    /// Remote clients record their own push-to-talk audio locally and upload only that bounded WAV
    /// to the host. The host validates the sender/range, performs Whisper with the host-only Groq
    /// key, then routes the transcript through the same ChatObserver command/conversation path.
    /// </summary>
    internal static class BuddyClientVoice
    {
        private const string MsgVoiceStart = "LethalAICrewmate_VoiceStart";
        private const string MsgVoiceChunk = "LethalAICrewmate_VoiceChunk";
        private const string GroqSttEndpoint = "https://api.groq.com/openai/v1/audio/transcriptions";
        private const int SampleRate = 16000;
        private const int MaxVoiceBytes = 300 * 1024;
        private const int VoiceChunkBytes = 7000;
        private const int MaxQueuedRemoteClips = 3;
        private const float MinRms = 0.008f;
        private const float TransferExpirySeconds = 15f;
        private const float SenderCooldownSeconds = 3f;
        private const int MaxIncomingTransfers = 4;

        private sealed class IncomingVoice
        {
            public ulong SenderId;
            public ulong TransferId;
            public byte[] Data;
            public int ReceivedBytes;
            public float ExpiresAt;
            public readonly HashSet<int> ReceivedOffsets = new HashSet<int>();
        }

        private sealed class RemoteVoiceRequest
        {
            public ulong SenderId;
            public byte[] Wav;
        }

        private static readonly Dictionary<ulong, IncomingVoice> IncomingBySender = new Dictionary<ulong, IncomingVoice>();
        private static readonly Dictionary<ulong, float> LastStartBySender = new Dictionary<ulong, float>();
        private static readonly Queue<RemoteVoiceRequest> HostQueue = new Queue<RemoteVoiceRequest>();
        private static readonly HashSet<ulong> QueuedSenders = new HashSet<ulong>();

        private static bool _registered;
        private static NetworkManager _registeredOn;
        private static NetworkManager _sessionManager;

        private static bool _clientRecording;
        private static bool _clientSending;
        private static string _clientMicDevice;
        private static AudioClip _clientClip;
        private static float _clientStartedAt;
        private static float _lastClientPttAt;
        private static float _clientHintCooldown;
        private static ulong _nextClientTransferId = 1;

        private static bool _hostBusy;

        internal static void Tick()
        {
            try
            {
                RegisterHandlers();

                var nm = NetworkManager.Singleton;
                if (nm == null || !nm.IsListening)
                {
                    ResetSession(nm);
                    return;
                }

                if (_sessionManager != nm)
                    ResetSession(nm);

                if (nm.IsServer)
                {
                    ExpireHostTransfers();
                    StartNextHostTranscription();
                }
                else if (nm.IsClient)
                {
                    TickClientCapture();
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"Buddy client voice tick: {ex.Message}");
            }
        }

        private static void ResetSession(NetworkManager manager)
        {
            _sessionManager = manager;
            IncomingBySender.Clear();
            LastStartBySender.Clear();
            HostQueue.Clear();
            QueuedSenders.Clear();
            _hostBusy = false;
            _clientRecording = false;
            _clientSending = false;
            _clientClip = null;
            _lastClientPttAt = -999f;
        }

        private static void RegisterHandlers()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || nm.CustomMessagingManager == null)
                return;
            if (_registered && _registeredOn == nm)
                return;

            try
            {
                if (_registeredOn != null && _registeredOn.CustomMessagingManager != null)
                {
                    try { _registeredOn.CustomMessagingManager.UnregisterNamedMessageHandler(MsgVoiceStart); } catch { }
                    try { _registeredOn.CustomMessagingManager.UnregisterNamedMessageHandler(MsgVoiceChunk); } catch { }
                }
            }
            catch { }

            _registered = false;
            _registeredOn = nm;
            try { nm.CustomMessagingManager.UnregisterNamedMessageHandler(MsgVoiceStart); } catch { }
            try { nm.CustomMessagingManager.UnregisterNamedMessageHandler(MsgVoiceChunk); } catch { }
            nm.CustomMessagingManager.RegisterNamedMessageHandler(MsgVoiceStart, OnVoiceStart);
            nm.CustomMessagingManager.RegisterNamedMessageHandler(MsgVoiceChunk, OnVoiceChunk);
            _registered = true;
            Plugin.Log?.LogInfo("Registered Buddy client voice-relay handlers.");
        }

        private static void TickClientCapture()
        {
            if (Plugin.VoiceEnabled == null || !Plugin.VoiceEnabled.Value)
                return;
            if (_clientSending)
                return;
            if (IsTextInputFocused())
                return;

            var key = Plugin.VoiceKey?.Value ?? KeyCode.V;
            float maxSec = Mathf.Clamp(Plugin.VoiceMaxSeconds?.Value ?? 8f, 1f, 12f);

            if (!_clientRecording && InputCompat.GetKeyDown(key))
            {
                if (Time.unscaledTime - _lastClientPttAt < 0.35f)
                    return;
                BeginClientRecord(maxSec);
            }
            else if (_clientRecording &&
                     (InputCompat.GetKeyUp(key) || Time.unscaledTime - _clientStartedAt >= maxSec))
            {
                _lastClientPttAt = Time.unscaledTime;
                EndClientRecordAndRelay();
            }
        }

        private static bool IsTextInputFocused()
        {
            try
            {
                var hud = HUDManager.Instance;
                return hud?.chatTextField != null && hud.chatTextField.isFocused;
            }
            catch
            {
                return false;
            }
        }

        private static void BeginClientRecord(float maxSec)
        {
            try
            {
                try { Microphone.End(_clientMicDevice); } catch { }
                _clientMicDevice = MicrophoneCapture.ResolveConfiguredDevice();
                int length = Mathf.Clamp(Mathf.CeilToInt(maxSec) + 1, 2, 13);
                _clientClip = Microphone.Start(_clientMicDevice, false, length, SampleRate);
                if (_clientClip == null)
                {
                    ClientHint("Microphone failed to start.");
                    return;
                }

                _clientRecording = true;
                _clientStartedAt = Time.unscaledTime;
                Plugin.Log?.LogInfo($"Client Buddy PTT start device='{_clientMicDevice ?? "default"}'.");
            }
            catch (Exception ex)
            {
                _clientRecording = false;
                Plugin.Log?.LogWarning($"Client Buddy PTT start: {ex.Message}");
            }
        }

        private static void EndClientRecordAndRelay()
        {
            if (!_clientRecording)
                return;
            _clientRecording = false;

            try
            {
                int samplePos = Microphone.GetPosition(_clientMicDevice);
                try { Microphone.End(_clientMicDevice); } catch { }
                float duration = Time.unscaledTime - _clientStartedAt;

                if (_clientClip == null || samplePos < SampleRate / 5 || duration < 0.35f)
                {
                    Plugin.Log?.LogInfo($"Client Buddy voice clip too short (samples={samplePos}, duration={duration:F2}s).");
                    return;
                }

                byte[] wav = MicrophoneCapture.EncodeAdaptiveMonoWav(
                    _clientClip, samplePos, out float inputRms, out float outputRms, out float gain);
                if (wav == null || wav.Length < 1000 || wav.Length > MaxVoiceBytes)
                {
                    ClientHint("Voice clip could not be sent.");
                    return;
                }
                if (!VoiceSignalMath.HasUsableSignal(inputRms))
                {
                    Plugin.Log?.LogWarning($"Client Buddy mic contains no usable signal (input rms={inputRms:F5}).");
                    ClientHint("Buddy heard silence. Set Voice.InputDevice if Windows chose the wrong mic.");
                    return;
                }

                Plugin.Log?.LogInfo($"Client Buddy mic accepted inputRms={inputRms:F5} outputRms={outputRms:F4} gain={gain:F1}.");

                if (Plugin.Host == null)
                    return;

                _clientSending = true;
                Plugin.Host.StartCoroutine(SendClientWav(wav));
            }
            catch (Exception ex)
            {
                _clientSending = false;
                Plugin.Log?.LogWarning($"Client Buddy PTT finish: {ex.Message}");
            }
        }

        private static IEnumerator SendClientWav(byte[] wav)
        {
            try
            {
                var nm = NetworkManager.Singleton;
                if (nm == null || nm.IsServer || !nm.IsClient || nm.CustomMessagingManager == null || !nm.IsListening)
                    yield break;

                ulong transferId = _nextClientTransferId++;
                if (_nextClientTransferId == 0) _nextClientTransferId = 1;

                using (var start = new FastBufferWriter(32, Allocator.Temp))
                {
                    start.WriteValueSafe(transferId);
                    start.WriteValueSafe(wav.Length);
                    nm.CustomMessagingManager.SendNamedMessage(
                        MsgVoiceStart,
                        NetworkManager.ServerClientId,
                        start,
                        NetworkDelivery.ReliableFragmentedSequenced);
                }

                int chunksThisFrame = 0;
                for (int offset = 0; offset < wav.Length; offset += VoiceChunkBytes)
                {
                    int len = Math.Min(VoiceChunkBytes, wav.Length - offset);
                    byte[] chunk = new byte[len];
                    Buffer.BlockCopy(wav, offset, chunk, 0, len);

                    using (var writer = new FastBufferWriter(len + 48, Allocator.Temp))
                    {
                        writer.WriteValueSafe(transferId);
                        writer.WriteValueSafe(offset);
                        writer.WriteValueSafe(len);
                        writer.WriteBytesSafe(chunk, len);
                        nm.CustomMessagingManager.SendNamedMessage(
                            MsgVoiceChunk,
                            NetworkManager.ServerClientId,
                            writer,
                            NetworkDelivery.ReliableFragmentedSequenced);
                    }

                    chunksThisFrame++;
                    if (chunksThisFrame >= 5)
                    {
                        chunksThisFrame = 0;
                        yield return null;
                    }
                }

                Plugin.Log?.LogInfo($"Relayed client Buddy voice to host ({wav.Length} bytes).");
            }
            finally
            {
                _clientSending = false;
            }
        }

        private static void OnVoiceStart(ulong senderId, FastBufferReader reader)
        {
            try
            {
                var nm = NetworkManager.Singleton;
                if (nm == null || !nm.IsServer || Plugin.AllowRemoteVoice?.Value != true || senderId == NetworkManager.ServerClientId ||
                    nm.CustomMessagingManager == null || !IsConnectedRemote(nm, senderId))
                    return;
                if (!NetMessenger.IsCompatibleClient(senderId))
                    return;

                reader.ReadValueSafe(out ulong transferId);
                reader.ReadValueSafe(out int totalBytes);
                if (transferId == 0 || totalBytes < 1000 || totalBytes > MaxVoiceBytes)
                    return;
                if (!IncomingBySender.ContainsKey(senderId) && IncomingBySender.Count >= MaxIncomingTransfers)
                    return;

                float now = Time.unscaledTime;
                if (LastStartBySender.TryGetValue(senderId, out float last) && now - last < SenderCooldownSeconds)
                    return;
                LastStartBySender[senderId] = now;

                IncomingBySender[senderId] = new IncomingVoice
                {
                    SenderId = senderId,
                    TransferId = transferId,
                    Data = new byte[totalBytes],
                    ReceivedBytes = 0,
                    ExpiresAt = now + TransferExpirySeconds
                };
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"Remote Buddy voice start: {ex.Message}");
            }
        }

        private static void OnVoiceChunk(ulong senderId, FastBufferReader reader)
        {
            try
            {
                var nm = NetworkManager.Singleton;
                if (nm == null || !nm.IsServer || Plugin.AllowRemoteVoice?.Value != true || senderId == NetworkManager.ServerClientId ||
                    !IsConnectedRemote(nm, senderId) || !NetMessenger.IsCompatibleClient(senderId))
                    return;
                if (!IncomingBySender.TryGetValue(senderId, out var incoming) || incoming == null)
                    return;

                reader.ReadValueSafe(out ulong transferId);
                reader.ReadValueSafe(out int offset);
                reader.ReadValueSafe(out int len);
                if (transferId != incoming.TransferId ||
                    !TransportValidation.IsExactChunk(incoming.Data.Length, VoiceChunkBytes, offset, len))
                    return;

                byte[] chunk = new byte[len];
                reader.ReadBytesSafe(ref chunk, len);
                if (incoming.ReceivedOffsets.Add(offset))
                {
                    Buffer.BlockCopy(chunk, 0, incoming.Data, offset, len);
                    incoming.ReceivedBytes += len;
                }
                incoming.ExpiresAt = Time.unscaledTime + TransferExpirySeconds;

                if (incoming.ReceivedBytes >= incoming.Data.Length)
                {
                    IncomingBySender.Remove(senderId);
                    string validation = "";
                    bool valid = TryValidateRemoteWav(incoming.Data, out validation);
                    if (valid && HostQueue.Count < MaxQueuedRemoteClips && !QueuedSenders.Contains(senderId))
                    {
                        HostQueue.Enqueue(new RemoteVoiceRequest
                        {
                            SenderId = senderId,
                            Wav = incoming.Data
                        });
                        QueuedSenders.Add(senderId);
                    }
                    else if (!string.IsNullOrEmpty(validation))
                        Plugin.Log?.LogInfo($"Rejected remote Buddy voice from client {senderId}: {validation}.");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"Remote Buddy voice chunk: {ex.Message}");
                IncomingBySender.Remove(senderId);
            }
        }

        private static void ExpireHostTransfers()
        {
            if (IncomingBySender.Count == 0)
                return;

            float now = Time.unscaledTime;
            var stale = new List<ulong>();
            foreach (var kv in IncomingBySender)
                if (kv.Value == null || now > kv.Value.ExpiresAt) stale.Add(kv.Key);
            foreach (ulong id in stale)
                IncomingBySender.Remove(id);
        }

        private static void StartNextHostTranscription()
        {
            if (_hostBusy || HostQueue.Count == 0 || Plugin.Host == null)
                return;
            if (!GroqSecrets.HasKey)
            {
                HostQueue.Clear();
                return;
            }

            var request = HostQueue.Dequeue();
            if (request != null) QueuedSenders.Remove(request.SenderId);
            if (request?.Wav == null || request.Wav.Length < 1000)
                return;

            _hostBusy = true;
            Plugin.Host.StartCoroutine(TranscribeRemote(request));
        }

        private static bool TryValidateRemoteWav(byte[] wav, out string reason)
        {
            return TransportValidation.TryValidateMonoPcm16Wav(
                wav, MaxVoiceBytes, 0.35f, 12.5f, MinRms, out reason);
        }

        private static IEnumerator TranscribeRemote(RemoteVoiceRequest request)
        {
            try
            {
                string model = ResolveSttModel();
                string boundary = "----LethalAIRemote" + UnityEngine.Random.Range(100000, 999999);
                byte[] body = BuildMultipart(boundary, request.Wav, model);

                using (var uwr = new UnityWebRequest(GroqSttEndpoint, "POST"))
                {
                    uwr.uploadHandler = new UploadHandlerRaw(body);
                    uwr.downloadHandler = new DownloadHandlerBuffer();
                    uwr.SetRequestHeader("Authorization", "Bearer " + GroqSecrets.CurrentKey);
                    uwr.SetRequestHeader("Content-Type", "multipart/form-data; boundary=" + boundary);
                    uwr.timeout = 20;

                    Plugin.Log?.LogInfo($"Groq remote STT -> client={request.SenderId} model={model} bytes={request.Wav.Length}");
                    yield return uwr.SendWebRequest();

                    bool ok = string.IsNullOrEmpty(uwr.error) && uwr.responseCode >= 200 && uwr.responseCode < 300;
                    if (!ok)
                    {
                        Plugin.Log?.LogWarning($"Remote Groq STT HTTP {uwr.responseCode}: {uwr.error}");
                        yield break;
                    }

                    string text = ParseTranscription(uwr.downloadHandler?.text);
                    if (string.IsNullOrWhiteSpace(text) || IsWhisperHallucination(text))
                        yield break;

                    HandleRemoteTranscript(request.SenderId, text.Trim());
                }
            }
            finally
            {
                _hostBusy = false;
            }
        }

        private static void HandleRemoteTranscript(ulong senderId, string text)
        {
            var player = ResolveRemotePlayer(senderId);
            if (player == null || string.IsNullOrWhiteSpace(text))
                return;

            try
            {
                if (HUDManager.Instance != null)
                    HUDManager.Instance.AddChatMessage(text, (player.playerUsername ?? "Player") + " (voice)");
            }
            catch { }

            string buddyName = Plugin.CrewmateName?.Value ?? "Buddy";
            string lower = text.ToLowerInvariant();
            string message = text;
            if (!lower.Contains(buddyName.ToLowerInvariant()) && !lower.Contains("buddy"))
                message = buddyName + " " + text;

            Plugin.Log?.LogInfo($"Remote STT client={senderId} player='{player.playerUsername}': {text}");
            ChatObserver.OnServerChat(message, (int)player.playerClientId);
        }

        private static PlayerControllerB ResolveRemotePlayer(ulong senderId)
        {
            try
            {
                var nm = NetworkManager.Singleton;
                if (nm == null || !nm.IsServer)
                    return null;
                if (!nm.ConnectedClients.TryGetValue(senderId, out var client) || client?.PlayerObject == null)
                    return null;
                return client.PlayerObject.GetComponent<PlayerControllerB>();
            }
            catch
            {
                return null;
            }
        }

        private static bool IsConnectedRemote(NetworkManager nm, ulong senderId)
        {
            if (nm == null || !nm.IsServer || senderId == NetworkManager.ServerClientId)
                return false;
            foreach (ulong id in nm.ConnectedClientsIds)
                if (id == senderId) return true;
            return false;
        }

        private static string ResolveSttModel()
        {
            string model = Plugin.SttModel?.Value;
            if (string.IsNullOrWhiteSpace(model) ||
                model.IndexOf("whisper", StringComparison.OrdinalIgnoreCase) < 0)
                return "whisper-large-v3-turbo";
            return model.Trim();
        }

        private static byte[] BuildMultipart(string boundary, byte[] wav, string model)
        {
            var sb = new StringBuilder();
            AppendPart(sb, boundary, "model", model);
            AppendPart(sb, boundary, "response_format", "json");
            AppendPart(sb, boundary, "language", "en");
            AppendPart(sb, boundary, "temperature", "0");
            AppendPart(sb, boundary, "prompt", "Lethal Company gameplay. Crew talking to Buddy AI. Commands: follow stay ship fetch scrap.");

            byte[] head = Encoding.UTF8.GetBytes(sb.ToString());
            byte[] fileHeader = Encoding.UTF8.GetBytes(
                "--" + boundary + "\r\n" +
                "Content-Disposition: form-data; name=\"file\"; filename=\"buddy-client.wav\"\r\n" +
                "Content-Type: audio/wav\r\n\r\n");
            byte[] mid = Encoding.UTF8.GetBytes("\r\n");
            byte[] end = Encoding.UTF8.GetBytes("--" + boundary + "--\r\n");

            byte[] all = new byte[head.Length + fileHeader.Length + wav.Length + mid.Length + end.Length];
            int offset = 0;
            Buffer.BlockCopy(head, 0, all, offset, head.Length); offset += head.Length;
            Buffer.BlockCopy(fileHeader, 0, all, offset, fileHeader.Length); offset += fileHeader.Length;
            Buffer.BlockCopy(wav, 0, all, offset, wav.Length); offset += wav.Length;
            Buffer.BlockCopy(mid, 0, all, offset, mid.Length); offset += mid.Length;
            Buffer.BlockCopy(end, 0, all, offset, end.Length);
            return all;
        }

        private static void AppendPart(StringBuilder sb, string boundary, string name, string value)
        {
            sb.Append("--").Append(boundary).Append("\r\n");
            sb.Append("Content-Disposition: form-data; name=\"").Append(name).Append("\"\r\n\r\n");
            sb.Append(value).Append("\r\n");
        }

        private static string ParseTranscription(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            int key = json.IndexOf("\"text\"", StringComparison.Ordinal);
            if (key < 0) return null;
            int colon = json.IndexOf(':', key + 6);
            if (colon < 0) return null;
            int i = colon + 1;
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            if (i >= json.Length || json[i] != '"') return null;
            i++;

            var sb = new StringBuilder();
            while (i < json.Length)
            {
                char c = json[i++];
                if (c == '\\' && i < json.Length)
                {
                    char n = json[i++];
                    if (n == '"') sb.Append('"');
                    else if (n == 'n' || n == 'r') sb.Append(' ');
                    else if (n == '\\') sb.Append('\\');
                    else sb.Append(n);
                }
                else if (c == '"') break;
                else sb.Append(c);
            }
            return sb.ToString().Trim();
        }

        private static bool IsWhisperHallucination(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return true;
            string t = text.Trim().ToLowerInvariant().TrimEnd('.', '!', '?', ' ');
            string[] bad =
            {
                "thank you", "thanks", "thanks for watching", "thank you for watching",
                "subscribe", "please subscribe", "bye", "goodbye", "you",
                "thank you very much", "thanks for listening", "amen", "null", ".", "..."
            };
            foreach (string badText in bad)
                if (t == badText) return true;
            return t.Length <= 2;
        }

        private static void ClientHint(string message)
        {
            if (Time.unscaledTime < _clientHintCooldown)
                return;
            _clientHintCooldown = Time.unscaledTime + 3f;
            try
            {
                if (HUDManager.Instance != null)
                    HUDManager.Instance.DisplayTip("Buddy", message, false, false, "BuddyClientVoiceTip");
            }
            catch
            {
                Plugin.Log?.LogInfo(message);
            }
        }

    }

}
