using System;
using System.Text;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace LethalAICrewmate
{
    public static class NetMessenger
    {
        public const string MsgCrewmateChat = "LethalAICrewmate_Chat";
        public const string MsgItemAttach = "LethalAICrewmate_ItemAttach";
        public const string MsgCrewmateSync = "LethalAICrewmate_Sync";

        private static bool _registered;
        private static NetworkManager _registeredOn;

        public static void TryRegisterHandlers()
        {
            try
            {
                var nm = NetworkManager.Singleton;
                if (nm == null) return;
                if (nm.CustomMessagingManager == null) return;

                if (_registered && _registeredOn == nm) return;

                // If NetworkManager was recreated, re-register
                if (_registered && _registeredOn != null && _registeredOn != nm)
                    _registered = false;

                var cmm = nm.CustomMessagingManager;
                try { cmm.UnregisterNamedMessageHandler(MsgCrewmateChat); } catch { /* ok */ }
                try { cmm.UnregisterNamedMessageHandler(MsgItemAttach); } catch { /* ok */ }
                try { cmm.UnregisterNamedMessageHandler(MsgCrewmateSync); } catch { /* ok */ }

                cmm.RegisterNamedMessageHandler(MsgCrewmateChat, OnCrewmateChat);
                cmm.RegisterNamedMessageHandler(MsgItemAttach, OnItemAttach);
                cmm.RegisterNamedMessageHandler(MsgCrewmateSync, OnCrewmateSync);

                _registered = true;
                _registeredOn = nm;
                Plugin.Log?.LogInfo("Registered CustomMessagingManager named message handlers.");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"TryRegisterHandlers: {ex}");
            }
        }

        public static void BroadcastCrewmateChat(string name, string text, Vector3 position, ulong crewmateNetId)
        {
            try
            {
                TryRegisterHandlers();
                var nm = NetworkManager.Singleton;
                if (nm == null || nm.CustomMessagingManager == null) return;
                if (!nm.IsServer && !nm.IsHost) return;

                byte[] nameBytes = Encoding.UTF8.GetBytes(name ?? "Buddy");
                byte[] textBytes = Encoding.UTF8.GetBytes(text ?? "");
                int size = 4 + nameBytes.Length + 4 + textBytes.Length + sizeof(float) * 3 + sizeof(ulong);

                using (var writer = new FastBufferWriter(Mathf.Max(size + 32, 256), Allocator.Temp))
                {
                    WriteString(writer, name ?? "Buddy");
                    WriteString(writer, text ?? "");
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
                if (nm == null || nm.CustomMessagingManager == null) return;
                if (!nm.IsServer && !nm.IsHost) return;

                using (var writer = new FastBufferWriter(64, Allocator.Temp))
                {
                    writer.WriteValueSafe(crewmateNetId);
                    writer.WriteValueSafe(itemNetId);
                    byte flag = attached ? (byte)1 : (byte)0;
                    writer.WriteValueSafe(flag);
                    nm.CustomMessagingManager.SendNamedMessageToAll(MsgItemAttach, writer, NetworkDelivery.Reliable);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"BroadcastItemAttach: {ex}");
            }
        }

        /// <summary>Tell all clients this NetworkObjectId is (or is no longer) our AI crewmate.</summary>
        public static void BroadcastCrewmateSync(ulong crewmateNetId, bool active)
        {
            try
            {
                TryRegisterHandlers();
                var nm = NetworkManager.Singleton;
                if (nm == null || nm.CustomMessagingManager == null) return;
                if (!nm.IsServer && !nm.IsHost) return;

                using (var writer = new FastBufferWriter(16, Allocator.Temp))
                {
                    writer.WriteValueSafe(crewmateNetId);
                    byte flag = active ? (byte)1 : (byte)0;
                    writer.WriteValueSafe(flag);
                    nm.CustomMessagingManager.SendNamedMessageToAll(MsgCrewmateSync, writer, NetworkDelivery.Reliable);
                }

                Plugin.Log?.LogInfo($"Broadcast crewmate sync id={crewmateNetId} active={active}");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"BroadcastCrewmateSync: {ex}");
            }
        }

        private static void OnCrewmateChat(ulong senderId, FastBufferReader reader)
        {
            try
            {
                // Host already displayed this locally in LlmClient.HandleAssistantReply;
                // SendNamedMessageToAll loops back to the host client, so skip it here.
                if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer) return;
                ReadString(reader, out string name);
                ReadString(reader, out string text);
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
                // Host already parented the item; skip loopback to avoid fighting host ownership.
                if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer) return;

                reader.ReadValueSafe(out ulong crewmateNetId);
                reader.ReadValueSafe(out ulong itemNetId);
                reader.ReadValueSafe(out byte flag);
                bool attached = flag != 0;
                CrewmateAI.ClientAttachItem(crewmateNetId, itemNetId, attached);
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
                reader.ReadValueSafe(out ulong crewmateNetId);
                reader.ReadValueSafe(out byte flag);
                bool active = flag != 0;

                if (active)
                    CrewmateRegistry.RegisterRemote(crewmateNetId);
                else
                    CrewmateRegistry.UnregisterRemote(crewmateNetId);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"OnCrewmateSync: {ex}");
            }
        }

        private static void WriteString(FastBufferWriter writer, string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value ?? "");
            int len = bytes.Length;
            writer.WriteValueSafe(len);
            if (len > 0)
                writer.WriteBytesSafe(bytes, len);
        }

        private static void ReadString(FastBufferReader reader, out string value)
        {
            reader.ReadValueSafe(out int len);
            if (len <= 0 || len > 4096)
            {
                value = "";
                return;
            }
            byte[] bytes = new byte[len];
            reader.ReadBytesSafe(ref bytes, len);
            value = Encoding.UTF8.GetString(bytes);
        }
    }
}
