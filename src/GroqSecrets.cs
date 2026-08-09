using System;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace LethalAICrewmate
{
    /// <summary>
    /// Keeps provider keys host-local. Keys saved from the menu are stored in the current Windows
    /// user's Credential Manager, never replicated to clients or written back to the config file.
    /// </summary>
    internal static class GroqSecrets
    {
        private const string GroqEnvironmentVariable = "LETHAL_AI_GROQ_API_KEY";
        private const string OpenAiEnvironmentVariable = "LETHAL_AI_OPENAI_API_KEY";
        private static string _openAiSessionKey = "";
        private static string _groqSessionKey = "";
        private static string _openAiStoredKey;
        private static string _groqStoredKey;
        internal static bool LastSavePersisted { get; private set; }

        internal static bool IsOpenAi => BuddyAiArchitecture.IsOpenAi(Plugin.Provider?.Value);
        internal static string ProviderName => IsOpenAi ? BuddyAiArchitecture.OpenAiProvider : BuddyAiArchitecture.GroqProvider;
        internal const string ChatEndpoint = "https://api.groq.com/openai/v1/chat/completions";
        internal const string SttEndpoint = "https://api.groq.com/openai/v1/audio/transcriptions";
        internal const string TtsEndpoint = "https://api.groq.com/openai/v1/audio/speech";
        internal static string ModelsEndpoint => IsOpenAi
            ? "https://api.openai.com/v1/models"
            : "https://api.groq.com/openai/v1/models";

        internal static string CurrentKey
        {
            get
            {
                string environmentKey = Normalize(Environment.GetEnvironmentVariable(
                    IsOpenAi ? OpenAiEnvironmentVariable : GroqEnvironmentVariable));
                if (!string.IsNullOrEmpty(environmentKey)) return environmentKey;
                string sessionKey = IsOpenAi ? _openAiSessionKey : _groqSessionKey;
                if (!string.IsNullOrEmpty(sessionKey)) return sessionKey;
                string storedKey = GetStoredKey(IsOpenAi);
                if (!string.IsNullOrEmpty(storedKey)) return storedKey;
                return "";
            }
        }

        internal static bool HasKey => !string.IsNullOrEmpty(CurrentKey);

        internal static bool SetFromMenu(string key)
        {
            key = Normalize(key);
            if (string.IsNullOrEmpty(key)) return false;

            bool openAi = IsOpenAi;
            if (openAi) _openAiSessionKey = key;
            else _groqSessionKey = key;
            LastSavePersisted = SetStoredKey(openAi, key);
            return true;
        }

        internal static bool ImportLegacyKey(bool openAi, string key)
        {
            key = Normalize(key);
            if (string.IsNullOrEmpty(key)) return false;
            if (openAi) _openAiSessionKey = key;
            else _groqSessionKey = key;
            bool stored = SetStoredKey(openAi, key);
            if (stored)
                Plugin.Log?.LogInfo((openAi ? "OpenAI" : "Groq") + " plaintext config key migrated to Windows Credential Manager.");
            return stored;
        }

        internal static void ClearMenuKey()
        {
            bool openAi = IsOpenAi;
            if (openAi) _openAiSessionKey = "";
            else _groqSessionKey = "";
            ClearStoredKey(openAi);
            Plugin.ClearLegacyPlaintextKey(openAi);
            LastSavePersisted = false;
        }

        private static string Normalize(string value)
        {
            string key = (value ?? "").Trim();
            if (key.Length > 256) return "";
            return key;
        }

        private static string GetStoredKey(bool openAi)
        {
            string cached = openAi ? _openAiStoredKey : _groqStoredKey;
            if (cached != null) return cached;

            string loaded = Normalize(WindowsCredentialStore.Read(CredentialTarget(openAi)));
            if (openAi) _openAiStoredKey = loaded;
            else _groqStoredKey = loaded;
            return loaded;
        }

        private static bool SetStoredKey(bool openAi, string key)
        {
            bool stored = WindowsCredentialStore.Write(CredentialTarget(openAi), key);
            if (openAi) _openAiStoredKey = stored ? key : "";
            else _groqStoredKey = stored ? key : "";

            if (!stored)
            {
                Plugin.Log?.LogWarning("Could not save API key to Windows Credential Manager; it will be kept for this session.");
            }
            return stored;
        }

        private static void ClearStoredKey(bool openAi)
        {
            WindowsCredentialStore.Delete(CredentialTarget(openAi));
            if (openAi) _openAiStoredKey = "";
            else _groqStoredKey = "";
        }

        private static string CredentialTarget(bool openAi)
        {
            return "LethalAICrewmate.ApiKey." + (openAi ? "OpenAI" : "Groq");
        }

        private static class WindowsCredentialStore
        {
            private const uint CredTypeGeneric = 1;
            private const uint CredPersistLocalMachine = 2;

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            private struct Credential
            {
                public uint Flags;
                public uint Type;
                public IntPtr TargetName;
                public IntPtr Comment;
                public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
                public uint CredentialBlobSize;
                public IntPtr CredentialBlob;
                public uint Persist;
                public uint AttributeCount;
                public IntPtr Attributes;
                public IntPtr TargetAlias;
                public IntPtr UserName;
            }

            [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool CredWrite(ref Credential userCredential, uint flags);

            [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credentialPtr);

            [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool CredDelete(string target, uint type, uint flags);

            [DllImport("advapi32.dll", SetLastError = true)]
            private static extern void CredFree(IntPtr buffer);

            internal static bool Write(string target, string secret)
            {
                if (string.IsNullOrEmpty(target) || string.IsNullOrEmpty(secret) || secret.Length > 256) return false;
                if (Application.platform != RuntimePlatform.WindowsPlayer && Application.platform != RuntimePlatform.WindowsEditor) return false;

                byte[] blob = Encoding.Unicode.GetBytes(secret);
                IntPtr targetPtr = IntPtr.Zero;
                IntPtr userPtr = IntPtr.Zero;
                IntPtr blobPtr = IntPtr.Zero;
                try
                {
                    targetPtr = Marshal.StringToCoTaskMemUni(target);
                    userPtr = Marshal.StringToCoTaskMemUni("LethalAICrewmate");
                    blobPtr = Marshal.AllocCoTaskMem(blob.Length);
                    Marshal.Copy(blob, 0, blobPtr, blob.Length);
                    var credential = new Credential
                    {
                        Type = CredTypeGeneric,
                        TargetName = targetPtr,
                        CredentialBlobSize = (uint)blob.Length,
                        CredentialBlob = blobPtr,
                        Persist = CredPersistLocalMachine,
                        UserName = userPtr
                    };
                    return CredWrite(ref credential, 0);
                }
                catch (Exception ex)
                {
                    Plugin.Log?.LogWarning("Windows Credential Manager write failed: " + ex.GetType().Name);
                    return false;
                }
                finally
                {
                    if (blobPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(blobPtr);
                    if (userPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(userPtr);
                    if (targetPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(targetPtr);
                }
            }

            internal static string Read(string target)
            {
                if (string.IsNullOrEmpty(target)) return "";
                if (Application.platform != RuntimePlatform.WindowsPlayer && Application.platform != RuntimePlatform.WindowsEditor) return "";

                IntPtr credentialPtr = IntPtr.Zero;
                try
                {
                    if (!CredRead(target, CredTypeGeneric, 0, out credentialPtr) || credentialPtr == IntPtr.Zero) return "";
                    var credential = (Credential)Marshal.PtrToStructure(credentialPtr, typeof(Credential));
                    if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0 || credential.CredentialBlobSize > 512) return "";
                    byte[] blob = new byte[credential.CredentialBlobSize];
                    Marshal.Copy(credential.CredentialBlob, blob, 0, blob.Length);
                    return Encoding.Unicode.GetString(blob);
                }
                catch (Exception ex)
                {
                    Plugin.Log?.LogWarning("Windows Credential Manager read failed: " + ex.GetType().Name);
                    return "";
                }
                finally
                {
                    if (credentialPtr != IntPtr.Zero) CredFree(credentialPtr);
                }
            }

            internal static void Delete(string target)
            {
                if (string.IsNullOrEmpty(target)) return;
                if (Application.platform != RuntimePlatform.WindowsPlayer && Application.platform != RuntimePlatform.WindowsEditor) return;
                try { CredDelete(target, CredTypeGeneric, 0); }
                catch (Exception ex) { Plugin.Log?.LogWarning("Windows Credential Manager delete failed: " + ex.GetType().Name); }
            }
        }
    }
}
