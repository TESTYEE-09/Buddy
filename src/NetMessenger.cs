using System;
using System.Collections.Generic;
using System.Text;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace LethalAICrewmate
{
    /// <summary>
    /// Host-authoritative multiplayer transport for Buddy state, chat, held items and speech.
    /// Remote clients never author AI state. Their only custom outbound message is a hello used
    /// to prove they run the same mod/protocol and request the current state for late joins.
    /// </summary>
    public static class NetMessenger
    {
        public const string MsgCrewmateChat = "LethalAICrewmate_Chat";
        public const string MsgItemAttach = "LethalAICrewmate_ItemAttach";
        public const string MsgCrewmateSync = "LethalAICrewmate_Sync";
        public const string MsgClientHello = "LethalAICrewmate_Hello";
        public const string MsgServerWelcome = "LethalAICrewmate_Welcome";
        public const string MsgTtsStart = "LethalAICrewmate_TtsStart";
        public const string MsgTtsChunk = "LethalAICrewmate_TtsChunk";

        // Increment whenever a wire format becomes incompatible.
        public const int ProtocolVersion = 5;

        private const float HelloIntervalSeconds = 2.5f;
        private const float MissingModGraceSeconds = 8f;
        private const float PendingAttachLifetimeSeconds = 12f;
        private const int MaxAudioBytes = 512 * 1024;
        private const int AudioChunkBytes = 8000;

        private static bool _registered;
        private static NetworkManager _registeredOn;
        private static NetworkManager _sessionManager;
        private static bool _helloAcked;
        private static float _nextHelloAt;
        private static ulong _nextAudioTransferId = 1;
        private static string _lastAnnouncedHostWarning = "";

        private sealed class PeerState
        {
            public bool HelloReceived;
            public bool Compatible;
            public string Version = "unknown";
            public int Protocol;
            public float FirstSeenAt;
        }

        private sealed class PendingItemAttach
        {
            public ulong CrewId;
            public ulong ItemId;
            public bool Attached;
            public float ExpiresAt;
        }

        private sealed class IncomingAudio
        {
            public ulong TransferId;
            public byte[] Data;
            public int SampleRate;
            public Vector3 Position;
            public int ReceivedBytes;
            public float ExpiresAt;
            public readonly HashSet<int> ReceivedOffsets = new HashSet<int>();
        }

        private static readonly Dictionary<ulong, PeerState> Peers = new Dictionary<ulong, PeerState>();
        private static readonly List<PendingItemAttach> PendingItemAttaches = new List<PendingItemAttach>();
        private static readonly Dictionary<ulong, IncomingAudio> IncomingAudioTransfers = new Dictionary<ulong, IncomingAudio>();

        public static string CompatibilityWarning { get; private set; } = "";
        public static string HostCompatibilityWarning { get; private set; } = "";

        /// <summary>Called every frame on every peer from PluginHost.</summary>
        public static void Tick()
        {
            try
            {
                TryRegisterHandlers();

                var nm = NetworkManager.Singleton;
                if (nm == null)
                {
                    ResetSessionState(null);
                    return;
                }

                if (_sessionManager != nm)
                    ResetSessionState(nm);

                if (!nm.IsListening)
                {
                    _helloAcked = false;
                    _nextHelloAt = 0f;
                    CompatibilityWarning = "";
                    HostCompatibilityWarning = "";
                    Peers.Clear();
                    PendingItemAttaches.Clear();
                    IncomingAudioTransfers.Clear();
                    return;
                }

                if (nm.IsServer)
                {
                    UpdateHostPeerTracking(nm);

                    // A new unmodded/mismatched client joining an active round is unsafe: on that
                    // client Buddy can fall back to hostile vanilla Masked behaviour. Remove Buddy
                    // immediately and let the spawn gate restore him once every peer is compatible.
                    if (!IsHostSessionReadyForBuddy() && CrewmateRegistry.GetPrimary() != null)
                    {
                        Plugin.Log?.LogWarning("Multiplayer compatibility changed; despawning Buddy until every client matches.");
                        CrewmateSpawner.DespawnAll();
                    }
                }
                else if (nm.IsClient)
                {
                    if (!_helloAcked && Time.unscaledTime >= _nextHelloAt)
                    {
                        _nextHelloAt = Time.unscaledTime + HelloIntervalSeconds;
                        SendClientHello();
                    }

                    RetryPendingItemAttaches(nm);
                    ExpireIncomingAudio();
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"NetMessenger.Tick: {ex}");
            }
        }

        private static void ResetSessionState(NetworkManager manager)
        {
            _sessionManager = manager;
            _helloAcked = false;
            _nextHelloAt = 0f;
            CompatibilityWarning = "";
            HostCompatibilityWarning = "";
            _lastAnnouncedHostWarning = "";
            Peers.Clear();
            PendingItemAttaches.Clear();
            IncomingAudioTransfers.Clear();
        }

        public static void TryRegisterHandlers()
        {
            try
            {
                var nm = NetworkManager.Singleton;
                if (nm == null || nm.CustomMessagingManager == null)
                    return;

                if (_registered && _registeredOn == nm)
                    return;

                _registered = false;
                _registeredOn = nm;

                var cmm = nm.CustomMessagingManager;
                SafeUnregister(cmm, MsgCrewmateChat);
                SafeUnregister(cmm, MsgItemAttach);
                SafeUnregister(cmm, MsgCrewmateSync);
                SafeUnregister(cmm, MsgClientHello);
                SafeUnregister(cmm, MsgServerWelcome);
                SafeUnregister(cmm, MsgTtsStart);
                SafeUnregister(cmm, MsgTtsChunk);

                cmm.RegisterNamedMessageHandler(MsgCrewmateChat, OnCrewmateChat);
                cmm.RegisterNamedMessageHandler(MsgItemAttach, OnItemAttach);
                cmm.RegisterNamedMessageHandler(MsgCrewmateSync, OnCrewmateSync);
                cmm.RegisterNamedMessageHandler(MsgClientHello, OnClientHello);
                cmm.RegisterNamedMessageHandler(MsgServerWelcome, OnServerWelcome);
                cmm.RegisterNamedMessageHandler(MsgTtsStart, OnTtsStart);
                cmm.RegisterNamedMessageHandler(MsgTtsChunk, OnTtsChunk);

                _registered = true;
                Plugin.Log?.LogInfo("Registered LethalAICrewmate multiplayer message handlers.");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"TryRegisterHandlers: {ex}");
            }
        }

        private static void SafeUnregister(CustomMessagingManager cmm, string name)
        {
            try { cmm.UnregisterNamedMessageHandler(name); } catch { /* not registered */ }
        }

        private static void UpdateHostPeerTracking(NetworkManager nm)
        {
            if (nm == null || !nm.IsServer)
                return;

            var currentRemoteIds = new List<ulong>();
            foreach (ulong id in nm.ConnectedClientsIds)
            {
                if (id == NetworkManager.ServerClientId)
                    continue;

                currentRemoteIds.Add(id);
                if (!Peers.ContainsKey(id))
                {
                    Peers[id] = new PeerState
                    {
                        FirstSeenAt = Time.unscaledTime
                    };
                    Plugin.Log?.LogInfo($"Waiting for LethalAICrewmate handshake from client {id}.");
                }
            }

            var stale = new List<ulong>();
            foreach (var kv in Peers)
            {
                if (!currentRemoteIds.Contains(kv.Key))
                    stale.Add(kv.Key);
            }
            foreach (ulong id in stale)
                Peers.Remove(id);

            string warning = "";
            foreach (ulong id in currentRemoteIds)
            {
                if (!Peers.TryGetValue(id, out var peer) || peer == null)
                    continue;
                if (peer.Compatible)
                    continue;

                if (!peer.HelloReceived)
                {
                    float age = Time.unscaledTime - peer.FirstSeenAt;
                    warning = age < MissingModGraceSeconds
                        ? $"Waiting for client {id} to load LethalAICrewmate..."
                        : $"Buddy disabled: client {id} has not loaded LethalAICrewmate {Plugin.ModVersion}.";
                }
                else
                {
                    warning = $"Buddy disabled: client {id} has mod {peer.Version}/protocol {peer.Protocol}; host is {Plugin.ModVersion}/{ProtocolVersion}.";
                }
                break;
            }

            HostCompatibilityWarning = warning;
            AnnounceHostWarningIfChanged(warning);
        }

        private static void AnnounceHostWarningIfChanged(string warning)
        {
            if (warning == _lastAnnouncedHostWarning)
                return;

            _lastAnnouncedHostWarning = warning;
            if (string.IsNullOrEmpty(warning))
            {
                Plugin.Log?.LogInfo("Multiplayer compatibility ready for Buddy.");
                return;
            }

            Plugin.Log?.LogWarning(warning);
            try
            {
                if (HUDManager.Instance != null)
                    HUDManager.Instance.AddChatMessage(warning, "LethalAICrewmate");
            }
            catch { /* HUD may not exist yet */ }
        }

        /// <summary>
        /// The spawn safety gate. In solo/host-only play it is immediately true. In multiplayer,
        /// every remote client must have completed the exact same version/protocol handshake.
        /// </summary>
        public static bool IsHostSessionReadyForBuddy()
        {
            try
            {
                var nm = NetworkManager.Singleton;
                if (nm == null || !nm.IsServer || !nm.IsListening)
                    return true;

                UpdateHostPeerTracking(nm);
                foreach (ulong id in nm.ConnectedClientsIds)
                {
                    if (id == NetworkManager.ServerClientId)
                        continue;
                    if (!Peers.TryGetValue(id, out var peer) || peer == null || !peer.Compatible)
                        return false;
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsConnectedRemoteClient(NetworkManager nm, ulong clientId)
        {
            if (nm == null || !nm.IsServer || clientId == NetworkManager.ServerClientId)
                return false;
            foreach (ulong id in nm.ConnectedClientsIds)
                if (id == clientId) return true;
            return false;
        }

        internal static bool IsCompatibleClient(ulong clientId)
        {
            return Peers.TryGetValue(clientId, out var peer) && peer != null && peer.Compatible;
        }

        internal static List<ulong> CompatibleClientIds()
        {
            var result = new List<ulong>();
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer)
                return result;

            UpdateHostPeerTracking(nm);
            foreach (ulong id in nm.ConnectedClientsIds)
            {
                if (id == NetworkManager.ServerClientId)
                    continue;
                if (IsCompatibleClient(id))
                    result.Add(id);
            }
            return result;
        }

        private static void SendClientHello()
        {
            try
            {
                var nm = NetworkManager.Singleton;
                if (nm == null || nm.CustomMessagingManager == null || !nm.IsClient || nm.IsServer)
                    return;

                using (var writer = new FastBufferWriter(128, Allocator.Temp))
                {
                    writer.WriteValueSafe(ProtocolVersion);
                    WriteString(writer, Plugin.ModVersion, 32);
                    nm.CustomMessagingManager.SendNamedMessage(
                        MsgClientHello,
                        NetworkManager.ServerClientId,
                        writer,
                        NetworkDelivery.Reliable);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"SendClientHello: {ex.Message}");
            }
        }

        private static void OnClientHello(ulong senderId, FastBufferReader reader)
        {
            try
            {
                var nm = NetworkManager.Singleton;
                if (nm == null || !nm.IsServer || nm.CustomMessagingManager == null || !IsConnectedRemoteClient(nm, senderId))
                    return;

                reader.ReadValueSafe(out int clientProtocol);
                if (!ReadString(reader, out string clientVersion, 64))
                    clientVersion = "unknown";

                bool compatible = clientProtocol == ProtocolVersion &&
                                  string.Equals(clientVersion, Plugin.ModVersion, StringComparison.OrdinalIgnoreCase);

                if (!Peers.TryGetValue(senderId, out var peer) || peer == null)
                {
                    peer = new PeerState { FirstSeenAt = Time.unscaledTime };
                    Peers[senderId] = peer;
                }
                peer.HelloReceived = true;
                peer.Compatible = compatible;
                peer.Version = clientVersion;
                peer.Protocol = clientProtocol;

                if (!compatible)
                    Plugin.Log?.LogWarning($"Client {senderId} LethalAICrewmate mismatch: mod={clientVersion}, protocol={clientProtocol}; host={Plugin.ModVersion}/{ProtocolVersion}.");
                else
                    Plugin.Log?.LogInfo($"Client {senderId} LethalAICrewmate handshake OK ({clientVersion}).");

                using (var writer = new FastBufferWriter(128, Allocator.Temp))
                {
                    writer.WriteValueSafe(ProtocolVersion);
                    WriteString(writer, Plugin.ModVersion, 32);
                    byte ok = compatible ? (byte)1 : (byte)0;
                    writer.WriteValueSafe(ok);
                    nm.CustomMessagingManager.SendNamedMessage(
                        MsgServerWelcome,
                        senderId,
                        writer,
                        NetworkDelivery.Reliable);
                }

                if (compatible)
                    SendCurrentStateToClient(senderId);

                UpdateHostPeerTracking(nm);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"OnClientHello: {ex}");
            }
        }

        private static void OnServerWelcome(ulong senderId, FastBufferReader reader)
        {
            try
            {
                if (!IsServerSender(senderId))
                    return;

                reader.ReadValueSafe(out int hostProtocol);
                if (!ReadString(reader, out string hostVersion, 64))
                    hostVersion = "unknown";
                reader.ReadValueSafe(out byte ok);

                _helloAcked = true;
                bool compatible = ok != 0 && hostProtocol == ProtocolVersion &&
                                  string.Equals(hostVersion, Plugin.ModVersion, StringComparison.OrdinalIgnoreCase);

                if (!compatible)
                {
                    CompatibilityWarning = $"Mod mismatch: host {hostVersion}/{hostProtocol}, you {Plugin.ModVersion}/{ProtocolVersion}.";
                    Plugin.Log?.LogWarning(CompatibilityWarning);
                }
                else
                {
                    CompatibilityWarning = "";
                    Plugin.Log?.LogInfo($"Multiplayer handshake OK with host ({hostVersion}).");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"OnServerWelcome: {ex}");
            }
        }

        private static void SendCurrentStateToClient(ulong clientId)
        {
            if (!IsCompatibleClient(clientId))
                return;

            try
            {
                foreach (var data in CrewmateRegistry.All)
                {
                    if (data == null || data.NetworkObjectId == 0)
                        continue;

                    SendCrewmateSyncToClient(clientId, data.NetworkObjectId, true);

                    if (data.HeldItem != null)
                    {
                        try
                        {
                            var itemNet = data.HeldItem.GetComponent<NetworkObject>();
                            if (itemNet != null && itemNet.IsSpawned)
                                SendItemAttachToClient(clientId, data.NetworkObjectId, itemNet.NetworkObjectId, true);
                        }
                        catch { /* item may despawn during sync */ }
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"SendCurrentStateToClient({clientId}): {ex.Message}");
            }
        }

        public static void BroadcastCrewmateChat(string name, string text, Vector3 position, ulong crewmateNetId)
        {
            try
            {
                var nm = NetworkManager.Singleton;
                if (nm == null || nm.CustomMessagingManager == null || !nm.IsServer)
                    return;

                name = ClampString(name ?? "Buddy", 64);
                text = ClampString(text ?? "", 2048);
                int size = 4 + Encoding.UTF8.GetByteCount(name) + 4 + Encoding.UTF8.GetByteCount(text) + sizeof(float) * 3 + sizeof(ulong);

                foreach (ulong clientId in CompatibleClientIds())
                {
                    using (var writer = new FastBufferWriter(Mathf.Max(size + 32, 256), Allocator.Temp))
                    {
                        WriteString(writer, name, 64);
                        WriteString(writer, text, 2048);
                        writer.WriteValueSafe(position);
                        writer.WriteValueSafe(crewmateNetId);
                        nm.CustomMessagingManager.SendNamedMessage(MsgCrewmateChat, clientId, writer, NetworkDelivery.Reliable);
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"BroadcastCrewmateChat: {ex}");
            }
        }

        public static void BroadcastItemAttach(ulong crewmateNetId, ulong itemNetId, bool attached)
        {
            try
            {
                var nm = NetworkManager.Singleton;
                if (nm == null || nm.CustomMessagingManager == null || !nm.IsServer)
                    return;

                foreach (ulong clientId in CompatibleClientIds())
                    SendItemAttachToClient(clientId, crewmateNetId, itemNetId, attached);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"BroadcastItemAttach: {ex}");
            }
        }

        private static void SendItemAttachToClient(ulong clientId, ulong crewmateNetId, ulong itemNetId, bool attached)
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || nm.CustomMessagingManager == null || !nm.IsServer || !IsCompatibleClient(clientId))
                return;

            using (var writer = new FastBufferWriter(64, Allocator.Temp))
            {
                writer.WriteValueSafe(crewmateNetId);
                writer.WriteValueSafe(itemNetId);
                byte flag = attached ? (byte)1 : (byte)0;
                writer.WriteValueSafe(flag);
                nm.CustomMessagingManager.SendNamedMessage(MsgItemAttach, clientId, writer, NetworkDelivery.Reliable);
            }
        }

        public static void BroadcastCrewmateSync(ulong crewmateNetId, bool active)
        {
            try
            {
                var nm = NetworkManager.Singleton;
                if (nm == null || nm.CustomMessagingManager == null || !nm.IsServer)
                    return;

                foreach (ulong clientId in CompatibleClientIds())
                    SendCrewmateSyncToClient(clientId, crewmateNetId, active);

                Plugin.Log?.LogInfo($"Broadcast crewmate sync id={crewmateNetId} active={active}");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"BroadcastCrewmateSync: {ex}");
            }
        }

        private static void SendCrewmateSyncToClient(ulong clientId, ulong crewmateNetId, bool active)
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || nm.CustomMessagingManager == null || !nm.IsServer || !IsCompatibleClient(clientId))
                return;

            using (var writer = new FastBufferWriter(24, Allocator.Temp))
            {
                writer.WriteValueSafe(crewmateNetId);
                byte flag = active ? (byte)1 : (byte)0;
                writer.WriteValueSafe(flag);
                nm.CustomMessagingManager.SendNamedMessage(MsgCrewmateSync, clientId, writer, NetworkDelivery.Reliable);
            }
        }

        /// <summary>
        /// Replicate already-generated speech as 16-bit mono PCM. The Groq key never leaves the host.
        /// Audio is chunked into small fragmented-reliable messages and capped to 512 KiB.
        /// </summary>
        public static void BroadcastTtsPcm(byte[] pcm16, int sampleRate, Vector3 position)
        {
            try
            {
                if (pcm16 == null || pcm16.Length == 0 || pcm16.Length > MaxAudioBytes || (pcm16.Length & 1) != 0)
                    return;
                if (sampleRate < 8000 || sampleRate > 48000)
                    return;

                var nm = NetworkManager.Singleton;
                if (nm == null || nm.CustomMessagingManager == null || !nm.IsServer)
                    return;

                var clients = CompatibleClientIds();
                if (clients.Count == 0)
                    return;

                ulong transferId = _nextAudioTransferId++;
                if (_nextAudioTransferId == 0) _nextAudioTransferId = 1;

                foreach (ulong clientId in clients)
                {
                    using (var start = new FastBufferWriter(64, Allocator.Temp))
                    {
                        start.WriteValueSafe(transferId);
                        start.WriteValueSafe(pcm16.Length);
                        start.WriteValueSafe(sampleRate);
                        start.WriteValueSafe(position);
                        nm.CustomMessagingManager.SendNamedMessage(MsgTtsStart, clientId, start, NetworkDelivery.ReliableFragmentedSequenced);
                    }

                    for (int offset = 0; offset < pcm16.Length; offset += AudioChunkBytes)
                    {
                        int len = Math.Min(AudioChunkBytes, pcm16.Length - offset);
                        byte[] chunk = new byte[len];
                        Buffer.BlockCopy(pcm16, offset, chunk, 0, len);

                        using (var writer = new FastBufferWriter(len + 48, Allocator.Temp))
                        {
                            writer.WriteValueSafe(transferId);
                            writer.WriteValueSafe(offset);
                            writer.WriteValueSafe(len);
                            writer.WriteBytesSafe(chunk, len);
                            nm.CustomMessagingManager.SendNamedMessage(
                                MsgTtsChunk,
                                clientId,
                                writer,
                                NetworkDelivery.ReliableFragmentedSequenced);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"BroadcastTtsPcm: {ex.Message}");
            }
        }

        private static bool IsServerSender(ulong senderId)
        {
            var nm = NetworkManager.Singleton;
            return nm != null && !nm.IsServer && senderId == NetworkManager.ServerClientId;
        }

        internal static bool CanAcceptServerStateMessage(ulong senderId)
        {
            return IsServerSender(senderId) && _helloAcked && string.IsNullOrEmpty(CompatibilityWarning);
        }

        private static void OnCrewmateChat(ulong senderId, FastBufferReader reader)
        {
            try
            {
                if (!CanAcceptServerStateMessage(senderId))
                    return;

                if (!ReadString(reader, out string name, 256)) return;
                if (!ReadString(reader, out string text, 8192)) return;
                reader.ReadValueSafe(out Vector3 position);
                reader.ReadValueSafe(out ulong crewmateNetId);
                if (crewmateNetId != 0)
                    CrewmateRegistry.RegisterRemote(crewmateNetId);
                Plugin.Log?.LogInfo($"Buddy chat packet received from server (netId={crewmateNetId}, chars={text.Length}).");
                bool displayed = ProximityChat.TryShowLocal(name, text, position, out string result);
                Plugin.Log?.LogInfo(displayed
                    ? "Buddy chat displayed on client."
                    : $"Buddy chat dropped on client: {result}.");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"OnCrewmateChat: {ex}");
            }
        }

        private static void OnItemAttach(ulong senderId, FastBufferReader reader)
        {
            try
            {
                if (!CanAcceptServerStateMessage(senderId))
                    return;

                reader.ReadValueSafe(out ulong crewmateNetId);
                reader.ReadValueSafe(out ulong itemNetId);
                reader.ReadValueSafe(out byte flag);
                QueuePendingItemAttach(crewmateNetId, itemNetId, flag != 0);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"OnItemAttach: {ex}");
            }
        }

        private static void QueuePendingItemAttach(ulong crewId, ulong itemId, bool attached)
        {
            if (crewId == 0 || itemId == 0)
                return;

            for (int i = PendingItemAttaches.Count - 1; i >= 0; i--)
            {
                var p = PendingItemAttaches[i];
                if (p.CrewId == crewId && p.ItemId == itemId)
                    PendingItemAttaches.RemoveAt(i);
            }

            PendingItemAttaches.Add(new PendingItemAttach
            {
                CrewId = crewId,
                ItemId = itemId,
                Attached = attached,
                ExpiresAt = Time.unscaledTime + PendingAttachLifetimeSeconds
            });

            RetryPendingItemAttaches(NetworkManager.Singleton);
        }

        private static void RetryPendingItemAttaches(NetworkManager nm)
        {
            if (PendingItemAttaches.Count == 0 || nm == null || nm.SpawnManager == null)
                return;

            for (int i = PendingItemAttaches.Count - 1; i >= 0; i--)
            {
                var pending = PendingItemAttaches[i];
                if (Time.unscaledTime > pending.ExpiresAt)
                {
                    PendingItemAttaches.RemoveAt(i);
                    continue;
                }

                if (!nm.SpawnManager.SpawnedObjects.ContainsKey(pending.CrewId) ||
                    !nm.SpawnManager.SpawnedObjects.ContainsKey(pending.ItemId))
                    continue;

                CrewmateAI.ClientAttachItem(pending.CrewId, pending.ItemId, pending.Attached);
                PendingItemAttaches.RemoveAt(i);
            }
        }

        private static void OnCrewmateSync(ulong senderId, FastBufferReader reader)
        {
            try
            {
                if (!CanAcceptServerStateMessage(senderId))
                    return;

                reader.ReadValueSafe(out ulong crewmateNetId);
                reader.ReadValueSafe(out byte flag);

                if (flag != 0)
                    CrewmateRegistry.RegisterRemote(crewmateNetId);
                else
                    CrewmateRegistry.UnregisterRemote(crewmateNetId);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"OnCrewmateSync: {ex}");
            }
        }

        private static void OnTtsStart(ulong senderId, FastBufferReader reader)
        {
            try
            {
                if (!CanAcceptServerStateMessage(senderId))
                    return;

                reader.ReadValueSafe(out ulong transferId);
                reader.ReadValueSafe(out int totalBytes);
                reader.ReadValueSafe(out int sampleRate);
                reader.ReadValueSafe(out Vector3 position);

                if (transferId == 0 || totalBytes <= 0 || totalBytes > MaxAudioBytes || (totalBytes & 1) != 0 ||
                    sampleRate < 8000 || sampleRate > 48000)
                {
                    Plugin.Log?.LogWarning("Rejected invalid Buddy audio transfer header.");
                    return;
                }

                if (!IncomingAudioTransfers.ContainsKey(transferId) && IncomingAudioTransfers.Count >= 2)
                {
                    Plugin.Log?.LogWarning("Rejected Buddy audio transfer: client transfer queue is full.");
                    return;
                }

                IncomingAudioTransfers[transferId] = new IncomingAudio
                {
                    TransferId = transferId,
                    Data = new byte[totalBytes],
                    SampleRate = sampleRate,
                    Position = position,
                    ReceivedBytes = 0,
                    ExpiresAt = Time.unscaledTime + 15f
                };
                Plugin.Log?.LogInfo($"Buddy TTS transfer started id={transferId} bytes={totalBytes} rate={sampleRate}.");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"OnTtsStart: {ex.Message}");
            }
        }

        private static void OnTtsChunk(ulong senderId, FastBufferReader reader)
        {
            try
            {
                if (!CanAcceptServerStateMessage(senderId))
                    return;

                reader.ReadValueSafe(out ulong transferId);
                reader.ReadValueSafe(out int offset);
                reader.ReadValueSafe(out int len);

                if (!IncomingAudioTransfers.TryGetValue(transferId, out var incoming) || incoming == null)
                    return;

                if (!TransportValidation.IsExactChunk(incoming.Data.Length, AudioChunkBytes, offset, len))
                {
                    Plugin.Log?.LogWarning("Rejected invalid Buddy audio chunk.");
                    return;
                }

                byte[] chunk = new byte[len];
                reader.ReadBytesSafe(ref chunk, len);

                if (incoming.ReceivedOffsets.Add(offset))
                {
                    Buffer.BlockCopy(chunk, 0, incoming.Data, offset, len);
                    incoming.ReceivedBytes += len;
                }
                incoming.ExpiresAt = Time.unscaledTime + 15f;

                if (incoming.ReceivedBytes == incoming.Data.Length)
                {
                    IncomingAudioTransfers.Remove(transferId);
                    var complete = incoming;
                    Plugin.Log?.LogInfo($"Buddy TTS transfer complete id={transferId} bytes={complete.Data.Length}.");
                    BuddyNetworkAudio.PlayReplicatedPcm(complete.Data, complete.SampleRate, complete.Position);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"OnTtsChunk: {ex.Message}");
            }
        }

        private static void ExpireIncomingAudio()
        {
            if (IncomingAudioTransfers.Count == 0) return;
            float now = Time.unscaledTime;
            var expired = new List<ulong>();
            foreach (var kv in IncomingAudioTransfers)
                if (kv.Value == null || now > kv.Value.ExpiresAt) expired.Add(kv.Key);
            foreach (ulong id in expired)
            {
                IncomingAudioTransfers.Remove(id);
                Plugin.Log?.LogWarning($"Buddy TTS transfer expired id={id}.");
            }
        }

        private static string ClampString(string value, int maxChars)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return value.Length <= maxChars ? value : value.Substring(0, maxChars);
        }

        private static void WriteString(FastBufferWriter writer, string value, int maxChars)
        {
            value = ClampString(value ?? "", maxChars);
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            int len = bytes.Length;
            writer.WriteValueSafe(len);
            if (len > 0)
                writer.WriteBytesSafe(bytes, len);
        }

        private static bool ReadString(FastBufferReader reader, out string value, int maxBytes)
        {
            value = "";
            reader.ReadValueSafe(out int len);
            if (len < 0 || len > maxBytes)
            {
                Plugin.Log?.LogWarning($"Rejected multiplayer string length {len} (max {maxBytes}).");
                return false;
            }
            if (len == 0)
                return true;

            byte[] bytes = new byte[len];
            reader.ReadBytesSafe(ref bytes, len);
            value = Encoding.UTF8.GetString(bytes);
            return true;
        }
    }
}
