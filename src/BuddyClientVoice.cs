using System;
using System.Collections.Generic;
using System.Text;
using GameNetcodeStuff;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace LethalAICrewmate
{
    /// <summary>
    /// Multiplayer voice relay for Buddy. Remote clients stream only bounded 16 kHz mono PCM chunks
    /// to the host while push-to-talk is held. The host validates identity/range/order and remains
    /// the only peer with an OpenAI key or Realtime socket.
    /// </summary>
    internal static class BuddyClientVoice
    {
        private const string MsgVoiceStart = "LethalAICrewmate_VoiceStart";
        private const string MsgVoiceChunk = "LethalAICrewmate_VoiceChunk";
        private const string MsgVoiceEnd = "LethalAICrewmate_VoiceEnd";
        private const string MsgVoiceHint = "LethalAICrewmate_VoiceHint";
        private const int RequestedSampleRate = 16000;
        private const int MaxVoiceBytes = 420 * 1024;
        private const int VoiceChunkBytes = 8000;
        private const float MinRms = 0.008f;
        private const float TransferExpirySeconds = 3f;
        private const float SenderCooldownSeconds = 0.75f;
        private const int MaxIncomingTransfers = 1;

        private sealed class IncomingVoice
        {
            public ulong SenderId;
            public ulong TransferId;
            public ulong StreamId;
            public int ReceivedBytes;
            public float StartedAt;
            public float ExpiresAt;
            public double PcmSquares;
            public long PcmSamples;
        }

        private static readonly Dictionary<ulong, IncomingVoice> IncomingBySender = new Dictionary<ulong, IncomingVoice>();
        private static readonly Dictionary<ulong, float> LastStartBySender = new Dictionary<ulong, float>();

        private static bool _registered;
        private static NetworkManager _registeredOn;
        private static NetworkManager _sessionManager;

        private static bool _clientRecording;
        private static string _clientMicDevice;
        private static AudioClip _clientClip;
        private static float _clientStartedAt;
        private static float _lastClientPttAt;
        private static float _clientHintCooldown;
        private static ulong _nextClientTransferId = 1;
        private static ulong _clientTransferId;
        private static KeyCode _clientRecordingKey;
        private static int _clientLastSampleFrame;
        private static int _clientSentBytes;
        private static float _clientStreamGain;
        private static double _clientInputSquares;
        private static long _clientInputFrames;

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
                    ExpireHostTransfers();
                else if (nm.IsClient)
                    TickClientCapture();
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"Buddy client voice tick: {ex.Message}");
            }
        }

        private static void ResetSession(NetworkManager manager)
        {
            foreach (var incoming in IncomingBySender.Values)
                if (incoming != null && incoming.StreamId != 0)
                    OpenAiRealtimeVoiceClient.AbortStreamingVoice(incoming.StreamId);
            IncomingBySender.Clear();
            LastStartBySender.Clear();

            if (_clientRecording)
            {
                try { Microphone.End(_clientMicDevice); } catch { }
                try { VoiceCoexistence.EndBuddyCapture(); } catch { }
            }
            CleanupClientCapture();
            _sessionManager = manager;
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
                    try { _registeredOn.CustomMessagingManager.UnregisterNamedMessageHandler(MsgVoiceEnd); } catch { }
                    try { _registeredOn.CustomMessagingManager.UnregisterNamedMessageHandler(MsgVoiceHint); } catch { }
                }
            }
            catch { }

            _registered = false;
            _registeredOn = nm;
            try { nm.CustomMessagingManager.UnregisterNamedMessageHandler(MsgVoiceStart); } catch { }
            try { nm.CustomMessagingManager.UnregisterNamedMessageHandler(MsgVoiceChunk); } catch { }
            try { nm.CustomMessagingManager.UnregisterNamedMessageHandler(MsgVoiceEnd); } catch { }
            try { nm.CustomMessagingManager.UnregisterNamedMessageHandler(MsgVoiceHint); } catch { }
            nm.CustomMessagingManager.RegisterNamedMessageHandler(MsgVoiceStart, OnVoiceStart);
            nm.CustomMessagingManager.RegisterNamedMessageHandler(MsgVoiceChunk, OnVoiceChunk);
            nm.CustomMessagingManager.RegisterNamedMessageHandler(MsgVoiceEnd, OnVoiceEnd);
            nm.CustomMessagingManager.RegisterNamedMessageHandler(MsgVoiceHint, OnVoiceHint);
            _registered = true;
            Plugin.Log?.LogInfo("Registered Buddy live client voice-relay handlers.");
        }

        private static void TickClientCapture()
        {
            if (_clientRecording)
            {
                if (Plugin.VoiceEnabled?.Value != true || !CrewmateSpawner.CanTalkToBuddy)
                {
                    AbortClientCapture("Buddy voice capture stopped.");
                    return;
                }

                FlushClientAudio(false);
                float maxSec = Mathf.Clamp(Plugin.VoiceMaxSeconds?.Value ?? 8f, 1f, 12f);
                if (InputCompat.GetKeyUp(_clientRecordingKey) || Time.unscaledTime - _clientStartedAt >= maxSec)
                {
                    _lastClientPttAt = Time.unscaledTime;
                    EndClientRecordAndRelay();
                }
                return;
            }

            if (Plugin.VoiceEnabled?.Value != true || !CrewmateSpawner.CanTalkToBuddy || IsTextInputFocused())
                return;

            var primary = Plugin.VoiceKey?.Value ?? KeyCode.B;
            var alternate = Plugin.VoiceAlternateKey?.Value ?? KeyCode.None;
            if (!(InputCompat.GetKeyDown(primary) ||
                (alternate != KeyCode.None && alternate != primary && InputCompat.GetKeyDown(alternate)))) return;
            if (Time.unscaledTime - _lastClientPttAt < 0.35f) return;

            BuddyNetworkAudio.StopPlayback();
            _clientRecordingKey = InputCompat.GetKeyDown(primary) ? primary : alternate;
            BeginClientRecord(Mathf.Clamp(Plugin.VoiceMaxSeconds?.Value ?? 8f, 1f, 12f));
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
                if (_clientClip != null)
                {
                    AudioClip old = _clientClip;
                    _clientClip = null;
                    UnityEngine.Object.Destroy(old);
                }

                _clientMicDevice = MicrophoneCapture.ResolveConfiguredDevice();
                VoiceCoexistence.BeginBuddyCapture(_clientMicDevice);
                int length = Mathf.Clamp(Mathf.CeilToInt(maxSec) + 1, 2, 13);
                _clientClip = Microphone.Start(_clientMicDevice, false, length, RequestedSampleRate);
                if (_clientClip == null)
                {
                    VoiceCoexistence.EndBuddyCapture();
                    ClientHint("Microphone failed to start.");
                    return;
                }

                _clientTransferId = _nextClientTransferId++;
                if (_nextClientTransferId == 0) _nextClientTransferId = 1;
                _clientRecording = true;
                _clientStartedAt = Time.unscaledTime;
                _clientLastSampleFrame = 0;
                _clientSentBytes = 0;
                _clientStreamGain = 0f;
                _clientInputSquares = 0d;
                _clientInputFrames = 0;
                SendClientStart(_clientTransferId);
                Plugin.Log?.LogInfo("Client Buddy live PTT started.");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"Client Buddy PTT start: {ex.Message}");
                AbortClientCapture(null);
            }
        }

        private static void FlushClientAudio(bool flushTail)
        {
            if (!_clientRecording || _clientClip == null || _clientTransferId == 0) return;
            int position = Microphone.GetPosition(_clientMicDevice);
            if (position < 0) return;
            if (position < _clientLastSampleFrame)
                throw new InvalidOperationException("Client microphone position wrapped during non-looping Buddy capture.");

            int available = position - _clientLastSampleFrame;
            int preferred = StreamingMicCapture.RecommendedSourceFrames(_clientClip);
            while (available >= preferred || (flushTail && available >= 2))
            {
                int frames = available >= preferred ? preferred : available;
                byte[] pcm = StreamingMicCapture.EncodeChunk(
                    _clientClip, _clientLastSampleFrame, frames, ref _clientStreamGain,
                    out float inputRms, out float outputRms);
                _clientLastSampleFrame += frames;
                available -= frames;
                if (pcm == null || pcm.Length < 4) continue;
                if (_clientSentBytes + pcm.Length > MaxVoiceBytes)
                    throw new InvalidOperationException("Client Buddy voice stream exceeded the bounded upload size.");

                _clientInputSquares += inputRms * inputRms * frames;
                _clientInputFrames += frames;
                SendClientChunk(_clientTransferId, _clientSentBytes, pcm);
                _clientSentBytes += pcm.Length;
                Plugin.Log?.LogDebug($"Client Buddy live chunk bytes={pcm.Length} inRms={inputRms:F5} outRms={outputRms:F4}.");
            }
        }

        private static void EndClientRecordAndRelay()
        {
            if (!_clientRecording) return;
            bool endSent = false;
            try
            {
                FlushClientAudio(true);
                float duration = Time.unscaledTime - _clientStartedAt;
                try { Microphone.End(_clientMicDevice); } catch { }
                VoiceCoexistence.EndBuddyCapture();

                float cumulativeRms = _clientInputFrames > 0
                    ? (float)Math.Sqrt(_clientInputSquares / _clientInputFrames)
                    : 0f;
                bool commit = _clientClip != null && _clientInputFrames >= RequestedSampleRate / 5 &&
                    duration >= 0.35f && _clientSentBytes >= RequestedSampleRate * 2 / 5 &&
                    VoiceSignalMath.HasUsableSignal(cumulativeRms);

                SendClientEnd(_clientTransferId, _clientSentBytes, commit);
                endSent = true;
                if (!commit)
                {
                    Plugin.Log?.LogInfo($"Client Buddy stream discarded duration={duration:F2}s rms={cumulativeRms:F5} bytes={_clientSentBytes}.");
                    if (!VoiceSignalMath.HasUsableSignal(cumulativeRms))
                        ClientHint("Buddy heard silence. Set Voice.InputDevice if Windows chose the wrong mic.");
                }
                else
                {
                    Plugin.Log?.LogInfo($"Client Buddy stream committed duration={duration:F2}s rms={cumulativeRms:F5} bytes={_clientSentBytes}.");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"Client Buddy PTT finish: {ex.Message}");
                if (!endSent && _clientTransferId != 0)
                    SendClientEnd(_clientTransferId, _clientSentBytes, false);
            }
            finally
            {
                CleanupClientCapture();
            }
        }

        private static void AbortClientCapture(string hint)
        {
            try
            {
                if (_clientTransferId != 0)
                    SendClientEnd(_clientTransferId, _clientSentBytes, false);
            }
            catch { }
            try { Microphone.End(_clientMicDevice); } catch { }
            try { VoiceCoexistence.EndBuddyCapture(); } catch { }
            CleanupClientCapture();
            if (!string.IsNullOrEmpty(hint)) ClientHint(hint);
        }

        private static void CleanupClientCapture()
        {
            _clientRecording = false;
            _clientTransferId = 0;
            _clientLastSampleFrame = 0;
            _clientSentBytes = 0;
            _clientStreamGain = 0f;
            _clientInputSquares = 0d;
            _clientInputFrames = 0;
            if (_clientClip != null)
            {
                AudioClip old = _clientClip;
                _clientClip = null;
                try { UnityEngine.Object.Destroy(old); } catch { }
            }
        }

        private static void SendClientStart(ulong transferId)
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || nm.IsServer || !nm.IsClient || nm.CustomMessagingManager == null || !nm.IsListening)
                throw new InvalidOperationException("Client network session is not available for Buddy voice.");
            using (var writer = new FastBufferWriter(24, Allocator.Temp))
            {
                writer.WriteValueSafe(transferId);
                nm.CustomMessagingManager.SendNamedMessage(
                    MsgVoiceStart,
                    NetworkManager.ServerClientId,
                    writer,
                    NetworkDelivery.ReliableSequenced);
            }
        }

        private static void SendClientChunk(ulong transferId, int offset, byte[] chunk)
        {
            if (chunk == null || chunk.Length < 4 || chunk.Length > VoiceChunkBytes || (chunk.Length & 1) != 0)
                throw new ArgumentException("Invalid Buddy live voice chunk.", nameof(chunk));
            var nm = NetworkManager.Singleton;
            if (nm == null || nm.IsServer || !nm.IsClient || nm.CustomMessagingManager == null || !nm.IsListening)
                throw new InvalidOperationException("Client network session ended during Buddy voice streaming.");

            using (var writer = new FastBufferWriter(chunk.Length + 48, Allocator.Temp))
            {
                writer.WriteValueSafe(transferId);
                writer.WriteValueSafe(offset);
                writer.WriteValueSafe(chunk.Length);
                writer.WriteBytesSafe(chunk, chunk.Length);
                nm.CustomMessagingManager.SendNamedMessage(
                    MsgVoiceChunk,
                    NetworkManager.ServerClientId,
                    writer,
                    NetworkDelivery.ReliableFragmentedSequenced);
            }
        }

        private static void SendClientEnd(ulong transferId, int totalBytes, bool commit)
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || nm.IsServer || !nm.IsClient || nm.CustomMessagingManager == null || !nm.IsListening)
                return;
            using (var writer = new FastBufferWriter(32, Allocator.Temp))
            {
                writer.WriteValueSafe(transferId);
                writer.WriteValueSafe(totalBytes);
                writer.WriteValueSafe(commit);
                nm.CustomMessagingManager.SendNamedMessage(
                    MsgVoiceEnd,
                    NetworkManager.ServerClientId,
                    writer,
                    NetworkDelivery.ReliableSequenced);
            }
        }

        private static void OnVoiceStart(ulong senderId, FastBufferReader reader)
        {
            try
            {
                var nm = NetworkManager.Singleton;
                if (nm == null || !nm.IsServer || Plugin.AllowRemoteVoice?.Value != true || senderId == NetworkManager.ServerClientId ||
                    nm.CustomMessagingManager == null || !IsConnectedRemote(nm, senderId) || !NetMessenger.IsCompatibleClient(senderId))
                    return;
                if (!CrewmateSpawner.CanTalkToBuddy) return;
                if (!IsSenderInBuddyRange(senderId))
                {
                    SendClientHint(senderId, "Move closer to Buddy before using push-to-talk.");
                    return;
                }

                reader.ReadValueSafe(out ulong transferId);
                if (transferId == 0 || IncomingBySender.ContainsKey(senderId)) return;
                if (IncomingBySender.Count >= MaxIncomingTransfers)
                {
                    SendClientHint(senderId, "Buddy is already listening to someone else.");
                    return;
                }

                float now = Time.unscaledTime;
                if (LastStartBySender.TryGetValue(senderId, out float last) && now - last < SenderCooldownSeconds)
                    return;
                LastStartBySender[senderId] = now;

                var player = ResolveRemotePlayer(senderId);
                if (player == null)
                {
                    SendClientHint(senderId, "Buddy couldn't identify your player slot.");
                    return;
                }

                LlmClient.NotePlayerInteraction();
                if (!OpenAiRealtimeVoiceClient.TryBeginStreamingVoice(
                    (int)player.playerClientId, player.playerUsername ?? ("Client " + senderId), out ulong streamId))
                {
                    SendClientHint(senderId, "Buddy is already listening to someone else.");
                    return;
                }

                IncomingBySender[senderId] = new IncomingVoice
                {
                    SenderId = senderId,
                    TransferId = transferId,
                    StreamId = streamId,
                    ReceivedBytes = 0,
                    StartedAt = now,
                    ExpiresAt = now + TransferExpirySeconds
                };
                Plugin.Log?.LogInfo($"Accepted live Buddy voice stream client={senderId} transfer={transferId}.");
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
                if (transferId != incoming.TransferId || offset != incoming.ReceivedBytes || len < 4 ||
                    len > VoiceChunkBytes || (len & 1) != 0 || incoming.ReceivedBytes + len > MaxVoiceBytes)
                {
                    AbortIncoming(senderId, incoming, "Buddy rejected malformed voice-stream data.");
                    return;
                }

                byte[] chunk = new byte[len];
                reader.ReadBytesSafe(ref chunk, len);
                float rms = StreamingMicCapture.CalculatePcm16Rms(chunk);
                int samples = len / 2;
                incoming.PcmSquares += rms * rms * samples;
                incoming.PcmSamples += samples;

                if (!OpenAiRealtimeVoiceClient.AppendStreamingVoice(incoming.StreamId, chunk))
                {
                    AbortIncoming(senderId, incoming, "Buddy's Realtime input stream stopped. Try again.");
                    return;
                }

                incoming.ReceivedBytes += len;
                incoming.ExpiresAt = Time.unscaledTime + TransferExpirySeconds;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"Remote Buddy voice chunk: {ex.Message}");
                if (IncomingBySender.TryGetValue(senderId, out var incoming))
                    AbortIncoming(senderId, incoming, null);
            }
        }

        private static void OnVoiceEnd(ulong senderId, FastBufferReader reader)
        {
            try
            {
                var nm = NetworkManager.Singleton;
                if (nm == null || !nm.IsServer || senderId == NetworkManager.ServerClientId ||
                    !IsConnectedRemote(nm, senderId) || !NetMessenger.IsCompatibleClient(senderId))
                    return;
                if (!IncomingBySender.TryGetValue(senderId, out var incoming) || incoming == null)
                    return;

                reader.ReadValueSafe(out ulong transferId);
                reader.ReadValueSafe(out int totalBytes);
                reader.ReadValueSafe(out bool commitRequested);
                IncomingBySender.Remove(senderId);

                float duration = Time.unscaledTime - incoming.StartedAt;
                float rms = incoming.PcmSamples > 0
                    ? (float)Math.Sqrt(incoming.PcmSquares / incoming.PcmSamples)
                    : 0f;
                bool valid = transferId == incoming.TransferId && totalBytes == incoming.ReceivedBytes &&
                    totalBytes >= StreamingMicCapture.WireRate * 2 / 5 && totalBytes <= MaxVoiceBytes &&
                    duration >= 0.35f && duration <= 12.75f && commitRequested &&
                    rms >= MinRms && IsSenderInBuddyRange(senderId);

                if (!valid)
                {
                    OpenAiRealtimeVoiceClient.AbortStreamingVoice(incoming.StreamId);
                    Plugin.Log?.LogInfo($"Rejected remote Buddy live voice client={senderId} duration={duration:F2}s rms={rms:F4} bytes={totalBytes} commit={commitRequested}.");
                    return;
                }

                if (!OpenAiRealtimeVoiceClient.EndStreamingVoice(incoming.StreamId))
                {
                    OpenAiRealtimeVoiceClient.AbortStreamingVoice(incoming.StreamId);
                    SendClientHint(senderId, "Buddy couldn't finish that Realtime turn. Try again.");
                    return;
                }
                Plugin.Log?.LogInfo($"Committed remote Buddy live voice client={senderId} duration={duration:F2}s bytes={totalBytes}.");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"Remote Buddy voice end: {ex.Message}");
                if (IncomingBySender.TryGetValue(senderId, out var incoming))
                    AbortIncoming(senderId, incoming, null);
            }
        }

        private static void AbortIncoming(ulong senderId, IncomingVoice incoming, string hint)
        {
            IncomingBySender.Remove(senderId);
            if (incoming != null && incoming.StreamId != 0)
                OpenAiRealtimeVoiceClient.AbortStreamingVoice(incoming.StreamId);
            if (!string.IsNullOrEmpty(hint)) SendClientHint(senderId, hint);
        }

        private static void ExpireHostTransfers()
        {
            if (IncomingBySender.Count == 0) return;
            float now = Time.unscaledTime;
            var stale = new List<ulong>();
            foreach (var kv in IncomingBySender)
                if (kv.Value == null || now > kv.Value.ExpiresAt || !IsConnectedRemote(NetworkManager.Singleton, kv.Key))
                    stale.Add(kv.Key);
            foreach (ulong id in stale)
            {
                if (IncomingBySender.TryGetValue(id, out var incoming))
                    AbortIncoming(id, incoming, null);
            }
        }

        private static PlayerControllerB ResolveRemotePlayer(ulong senderId)
        {
            try
            {
                var nm = NetworkManager.Singleton;
                if (nm == null || !nm.IsServer)
                    return null;
                if (nm.ConnectedClients.TryGetValue(senderId, out var client) && client?.PlayerObject != null)
                {
                    var direct = client.PlayerObject.GetComponent<PlayerControllerB>();
                    if (direct != null) return direct;
                }

                var players = StartOfRound.Instance?.allPlayerScripts;
                if (players != null)
                {
                    for (int i = 0; i < players.Length; i++)
                    {
                        var player = players[i];
                        if (player != null && player.playerClientId == senderId)
                            return player;
                    }
                }
                return null;
            }
            catch
            {
                return null;
            }
        }

        private static bool IsSenderInBuddyRange(ulong senderId)
        {
            try
            {
                var player = ResolveRemotePlayer(senderId);
                // In orbit Buddy is a ship-wide voice terminal and intentionally has no body.
                if (StartOfRound.Instance?.inShipPhase == true) return player != null;
                var buddy = CrewmateRegistry.GetPrimary()?.Enemy;
                if (player == null || buddy == null) return false;
                float configured = Plugin.ChatTriggerRange?.Value ?? 60f;
                float range = Mathf.Clamp(configured <= 0f ? 60f : configured, 5f, 80f);
                return Vector3.Distance(player.transform.position, buddy.transform.position) <= range;
            }
            catch { return false; }
        }

        private static bool IsConnectedRemote(NetworkManager nm, ulong senderId)
        {
            if (nm == null || !nm.IsServer || senderId == NetworkManager.ServerClientId)
                return false;
            foreach (ulong id in nm.ConnectedClientsIds)
                if (id == senderId) return true;
            return false;
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

        private static void SendClientHint(ulong clientId, string message)
        {
            try
            {
                var nm = NetworkManager.Singleton;
                if (nm == null || !nm.IsServer || nm.CustomMessagingManager == null ||
                    !IsConnectedRemote(nm, clientId) || !NetMessenger.IsCompatibleClient(clientId))
                    return;

                byte[] bytes = Encoding.UTF8.GetBytes(message ?? "Buddy could not process that voice stream.");
                if (bytes.Length > 220) Array.Resize(ref bytes, 220);
                using (var writer = new FastBufferWriter(bytes.Length + 16, Allocator.Temp))
                {
                    writer.WriteValueSafe(bytes.Length);
                    writer.WriteBytesSafe(bytes, bytes.Length);
                    nm.CustomMessagingManager.SendNamedMessage(MsgVoiceHint, clientId, writer, NetworkDelivery.Reliable);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"Buddy voice hint send: {ex.Message}");
            }
        }

        private static void OnVoiceHint(ulong senderId, FastBufferReader reader)
        {
            try
            {
                var nm = NetworkManager.Singleton;
                if (nm == null || !nm.IsClient || nm.IsServer || !NetMessenger.CanAcceptServerStateMessage(senderId))
                    return;
                reader.ReadValueSafe(out int len);
                if (len <= 0 || len > 220) return;
                byte[] bytes = new byte[len];
                reader.ReadBytesSafe(ref bytes, len);
                ClientHint(Encoding.UTF8.GetString(bytes));
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"Buddy voice hint receive: {ex.Message}");
            }
        }
    }
}
