using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using GameNetcodeStuff;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace LethalAICrewmate
{
    /// <summary>
    /// v1.4.3 multiplayer voice relay.
    /// Remote clients record their own push-to-talk audio locally and upload only that bounded WAV
    /// to the host. The host validates the sender/range, then uses either the host-only OpenAI
    /// gpt-realtime-2.1-mini session end-to-end.
    /// </summary>
    internal static class BuddyClientVoice
    {
        private const string MsgVoiceStart = "LethalAICrewmate_VoiceStart";
        private const string MsgVoiceChunk = "LethalAICrewmate_VoiceChunk";
        private const string MsgVoiceHint = "LethalAICrewmate_VoiceHint";
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
        private static KeyCode _clientRecordingKey;

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
                    StartNextHostRealtime();
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
            if (_clientClip != null)
            {
                AudioClip old = _clientClip;
                _clientClip = null;
                UnityEngine.Object.Destroy(old);
            }
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
                    try { _registeredOn.CustomMessagingManager.UnregisterNamedMessageHandler(MsgVoiceHint); } catch { }
                }
            }
            catch { }

            _registered = false;
            _registeredOn = nm;
            try { nm.CustomMessagingManager.UnregisterNamedMessageHandler(MsgVoiceStart); } catch { }
            try { nm.CustomMessagingManager.UnregisterNamedMessageHandler(MsgVoiceChunk); } catch { }
            try { nm.CustomMessagingManager.UnregisterNamedMessageHandler(MsgVoiceHint); } catch { }
            nm.CustomMessagingManager.RegisterNamedMessageHandler(MsgVoiceStart, OnVoiceStart);
            nm.CustomMessagingManager.RegisterNamedMessageHandler(MsgVoiceChunk, OnVoiceChunk);
            nm.CustomMessagingManager.RegisterNamedMessageHandler(MsgVoiceHint, OnVoiceHint);
            _registered = true;
            Plugin.Log?.LogInfo("Registered Buddy client voice-relay handlers.");
        }

        private static void TickClientCapture()
        {
            if (Plugin.VoiceEnabled == null || !Plugin.VoiceEnabled.Value)
                return;
            if (!CrewmateSpawner.CanTalkToBuddy)
                return;
            if (_clientSending)
                return;
            if (IsTextInputFocused())
                return;

            var primary = Plugin.VoiceKey?.Value ?? KeyCode.B;
            var alternate = Plugin.VoiceAlternateKey?.Value ?? KeyCode.None;
            float maxSec = Mathf.Clamp(Plugin.VoiceMaxSeconds?.Value ?? 8f, 1f, 12f);

            if (!_clientRecording && (InputCompat.GetKeyDown(primary) ||
                                      (alternate != KeyCode.None && alternate != primary && InputCompat.GetKeyDown(alternate))))
            {
                BuddyNetworkAudio.StopPlayback();
                if (Time.unscaledTime - _lastClientPttAt < 0.35f)
                    return;
                _clientRecordingKey = InputCompat.GetKeyDown(primary) ? primary : alternate;
                BeginClientRecord(maxSec);
            }
            else if (_clientRecording &&
                     (InputCompat.GetKeyUp(_clientRecordingKey) || Time.unscaledTime - _clientStartedAt >= maxSec))
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
                if (_clientClip != null)
                {
                    AudioClip old = _clientClip;
                    _clientClip = null;
                    UnityEngine.Object.Destroy(old);
                }
                _clientMicDevice = MicrophoneCapture.ResolveConfiguredDevice();
                // Talking to Buddy must not take the crew's voice chat away from this player.
                VoiceCoexistence.BeginBuddyCapture(_clientMicDevice);
                int length = Mathf.Clamp(Mathf.CeilToInt(maxSec) + 1, 2, 13);
                _clientClip = Microphone.Start(_clientMicDevice, false, length, SampleRate);
                if (_clientClip == null)
                {
                    VoiceCoexistence.EndBuddyCapture();
                    ClientHint("Microphone failed to start.");
                    return;
                }

                _clientRecording = true;
                _clientStartedAt = Time.unscaledTime;
                ClientHint("Recording for Buddy… release the key to send.");
                Plugin.Log?.LogInfo("Client Buddy PTT recording started.");
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
                VoiceCoexistence.EndBuddyCapture();
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
                if (!CrewmateSpawner.CanTalkToBuddy) return;
                if (!IsSenderInBuddyRange(senderId))
                {
                    SendClientHint(senderId, "Move closer to Buddy before using push-to-talk.");
                    return;
                }

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

        private static void StartNextHostRealtime()
        {
            if (_hostBusy || HostQueue.Count == 0 || Plugin.Host == null)
                return;
            if (!OpenAiSecrets.HasKey)
            {
                HostQueue.Clear();
                return;
            }

            var request = HostQueue.Dequeue();
            if (request != null) QueuedSenders.Remove(request.SenderId);
            if (request?.Wav == null || request.Wav.Length < 1000)
                return;

            LlmClient.NotePlayerInteraction();
            _hostBusy = true;
            Plugin.Host.StartCoroutine(SendRemoteRealtime(request));
        }

        private static bool TryValidateRemoteWav(byte[] wav, out string reason)
        {
            return TransportValidation.TryValidateMonoPcm16Wav(
                wav, MaxVoiceBytes, 0.35f, 12.5f, MinRms, out reason);
        }

        private static IEnumerator SendRemoteRealtime(RemoteVoiceRequest request)
        {
            try
            {
                var player = ResolveRemotePlayer(request.SenderId);
                int playerId = player != null ? (int)player.playerClientId : (int)request.SenderId;
                string playerName = player?.playerUsername ?? ("Client " + request.SenderId);
                if (OpenAiRealtimeVoiceClient.EnqueueWav(request.Wav, playerId, playerName))
                {
                    Plugin.Log?.LogInfo($"Queued remote native realtime voice turn client={request.SenderId}.");
                    yield break;
                }
                SendClientHint(request.SenderId, "Buddy couldn't start the OpenAI Realtime turn. Try again.");
            }
            finally
            {
                _hostBusy = false;
                Plugin.Log?.LogInfo($"Remote Buddy Realtime turn queued client={request?.SenderId}; queued={HostQueue.Count}.");
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
            if (!string.IsNullOrEmpty(message) && message.StartsWith("Recording for Buddy", StringComparison.Ordinal)) return;
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

                byte[] bytes = Encoding.UTF8.GetBytes(message ?? "Buddy could not process that voice clip.");
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
