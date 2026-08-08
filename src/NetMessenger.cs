using System;
using System.Text;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace LethalAICrewmate
{
    /// <summary>
    /// Lightweight host-authoritative multiplayer transport.
    /// Clients never send AI commands/state directly; their only custom message is a hello used
    /// to negotiate protocol/version and request the current Buddy state for late joins.
    /// </summary>
    public static class NetMessenger
    {
        public const string MsgCrewmateChat = "LethalAICrewmate_Chat";
        public const string MsgItemAttach = "LethalAICrewmate_ItemAttach";
        public const string MsgCrewmateSync = "LethalAICrewmate_Sync";
        public const string MsgClientHello = "LethalAICrewmate_Hello";
        public const string MsgServerWelcome = "LethalAICrewmate_Welcome";

        // Increment only when the wire format becomes incompatible.
        public const int ProtocolVersion = 2;

        private static bool _registered;
        private static NetworkManager _registeredOn;
        private static NetworkManager _helloManager;
        private static bool _helloAcked;
        private static float _nextHelloAt;

        public static string CompatibilityWarning { get; private set; } = "";

        /// <summary>Called every frame on every peer from PluginHost.</summary>
        public static void Tick()
        {
            try
            {
                TryRegisterHandlers();

                var nm = NetworkManager.Singleton;
                if (nm == null)
                {
                    ResetHelloState(null);
                    return;
                }

                if (_helloManager != nm)
                    ResetHelloState(nm);

                if (!nm.IsListening)
                {
                    _helloAcked = false;
                    _nextHelloAt = 0f;
                    CompatibilityWarning = "";
                    return;
                }

                // Host/server owns AI and answers hello requests. Remote clients only request state.
                if (nm.IsClient && !nm.IsServer && !_helloAcked && Time.unscaledTime >= _nextHelloAt)
                {
                    _nextHelloAt = Time.unscaledTime + 2.5f;
                    SendClientHello();
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"NetMessenger.Tick: {ex}");
            }
        }

        private static void ResetHelloState(NetworkManager manager)
        {
            _helloManager = manager;
            _helloAcked = false;
            _nextHelloAt = 0f;
            CompatibilityWarning = "";
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

                if (_registeredOn != nm)
                {
                    _registered = false;
                    _registeredOn = nm;
                }

                var cmm = nm.CustomMessagingManager;
                SafeUnregister(cmm, MsgCrewmateChat);
                SafeUnregister(cmm, MsgItemAttach);
                SafeUnregister(cmm, MsgCrewmateSync);
                SafeUnregister(cmm, MsgClientHello);
                SafeUnregister(cmm, MsgServerWelcome);

                cmm.RegisterNamedMessageHandler(MsgCrewmateChat, OnCrewmateChat);
                cmm.RegisterNamedMessageHandler(MsgItemAttach, OnItemAttach);
                cmm.RegisterNamedMessageHandler(MsgCrewmateSync, OnCrewmateSync);
                cmm.RegisterNamedMessageHandler(MsgClientHello, OnClientHello);
                cmm.RegisterNamedMessageHandler(MsgServerWelcome, OnServerWelcome);

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
                if (nm == null || !nm.IsServer || nm.CustomMessagingManager == null)
                    return;

                reader.ReadValueSafe(out int clientProtocol);
                if (!ReadString(reader, out string clientVersion, 32))
                    clientVersion = "unknown";

                bool compatible = clientProtocol == ProtocolVersion &&
                                  string.Equals(clientVersion, Plugin.ModVersion, StringComparison.OrdinalIgnoreCase);

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

                // This is the important late-join path: send whatever Buddy state exists now,
                // rather than assuming the client was present for the original spawn broadcast.
                SendCurrentStateToClient(senderId);
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
                if (!CanAcceptServerMessage(senderId))
                    return;

                reader.ReadValueSafe(out int hostProtocol);
                if (!ReadString(reader, out string hostVersion, 32))
                    hostVersion = "unknown";
                reader.ReadValueSafe(out byte ok);

                _helloAcked = true;
                bool compatible = ok != 0 && hostProtocol == ProtocolVersion &&
                                  string.Equals(hostVersion, Plugin.ModVersion, StringComparison.OrdinalIgnoreCase);

                if (!compatible)
                {
                    CompatibilityWarning = $"Mod mismatch: host {hostVersion}, you {Plugin.ModVersion}.";
                    Plugin.Log?.LogWarning(CompatibilityWarning + $" Protocol host={hostProtocol}, local={ProtocolVersion}.");
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
                TryRegisterHandlers();
                var nm = NetworkManager.Singleton;
                if (nm == null || nm.CustomMessagingManager == null || !nm.IsServer)
                    return;

                name = ClampString(name ?? "Buddy", 64);
                text = ClampString(text ?? "", 2048);
                int size = 4 + Encoding.UTF8.GetByteCount(name) + 4 + Encoding.UTF8.GetByteCount(text) + sizeof(float) * 3 + sizeof(ulong);

                using (var writer = new FastBufferWriter(Mathf.Max(size + 32, 256), Allocator.Temp))
                {
                    WriteString(writer, name, 64);
                    WriteString(writer, text, 2048);
                    writer.WriteValueSafe(position);
                    writer.WriteValueSafe(crewmateNetId);
                    nm.CustomMessagingManager.SendNamedMessageToAll(MsgCrewmateChat, writer, NetworkDelivery.Reliable);
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
                TryRegisterHandlers();
                var nm = NetworkManager.Singleton;
                if (nm == null || nm.CustomMessagingManager == null || !nm.IsServer)
                    return;

                using (var writer = new FastBufferWriter(64, Allocator.Temp))
                {
                    WriteItemAttachPayload(writer, crewmateNetId, itemNetId, attached);
                    nm.CustomMessagingManager.SendNamedMessageToAll(MsgItemAttach, writer, NetworkDelivery.Reliable);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"BroadcastItemAttach: {ex}");
            }
        }

        private static void SendItemAttachToClient(ulong clientId, ulong crewmateNetId, ulong itemNetId, bool attached)
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || nm.CustomMessagingManager == null || !nm.IsServer)
                return;

            using (var writer = new FastBufferWriter(64, Allocator.Temp))
            {
                WriteItemAttachPayload(writer, crewmateNetId, itemNetId, attached);
                nm.CustomMessagingManager.SendNamedMessage(MsgItemAttach, clientId, writer, NetworkDelivery.Reliable);
            }
        }

        private static void WriteItemAttachPayload(FastBufferWriter writer, ulong crewmateNetId, ulong itemNetId, bool attached)
        {
            writer.WriteValueSafe(crewmateNetId);
            writer.WriteValueSafe(itemNetId);
            byte flag = attached ? (byte)1 : (byte)0;
            writer.WriteValueSafe(flag);
        }

        /// <summary>Tell clients this NetworkObjectId is (or is no longer) our AI crewmate.</summary>
        public static void BroadcastCrewmateSync(ulong crewmateNetId, bool active)
        {
            try
            {
                TryRegisterHandlers();
                var nm = NetworkManager.Singleton;
                if (nm == null || nm.CustomMessagingManager == null || !nm.IsServer)
                    return;

                using (var writer = new FastBufferWriter(16, Allocator.Temp))
                {
                    WriteCrewmateSyncPayload(writer, crewmateNetId, active);
                    nm.CustomMessagingManager.SendNamedMessageToAll(MsgCrewmateSync, writer, NetworkDelivery.Reliable);
                }

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
            if (nm == null || nm.CustomMessagingManager == null || !nm.IsServer)
                return;

            using (var writer = new FastBufferWriter(16, Allocator.Temp))
            {
                WriteCrewmateSyncPayload(writer, crewmateNetId, active);
                nm.CustomMessagingManager.SendNamedMessage(MsgCrewmateSync, clientId, writer, NetworkDelivery.Reliable);
            }
        }

        private static void WriteCrewmateSyncPayload(FastBufferWriter writer, ulong crewmateNetId, bool active)
        {
            writer.WriteValueSafe(crewmateNetId);
            byte flag = active ? (byte)1 : (byte)0;
            writer.WriteValueSafe(flag);
        }

        private static bool CanAcceptServerMessage(ulong senderId)
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || nm.IsServer)
                return false;
            return senderId == NetworkManager.ServerClientId;
        }

        private static void OnCrewmateChat(ulong senderId, FastBufferReader reader)
        {
            try
            {
                if (!CanAcceptServerMessage(senderId))
                    return;

                if (!ReadString(reader, out string name, 64)) return;
                if (!ReadString(reader, out string text, 2048)) return;
                reader.ReadValueSafe(out Vector3 position);
                reader.ReadValueSafe(out ulong crewmateNetId);
                ProximityChat.TryShowLocal(name, text, position);
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
                if (!CanAcceptServerMessage(senderId))
                    return;

                reader.ReadValueSafe(out ulong crewmateNetId);
                reader.ReadValueSafe(out ulong itemNetId);
                reader.ReadValueSafe(out byte flag);
                CrewmateAI.ClientAttachItem(crewmateNetId, itemNetId, flag != 0);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"OnItemAttach: {ex}");
            }
        }

        private static void OnCrewmateSync(ulong senderId, FastBufferReader reader)
        {
            try
            {
                if (!CanAcceptServerMessage(senderId))
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
