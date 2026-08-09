using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace LethalAICrewmate
{
    /// <summary>
    /// Groq Orpheus TTS. Orpheus returns streaming WAVs (RIFF size = 0xFFFFFFFF)
    /// which break Unity's decoder — we normalize sizes then play PCM.
    /// </summary>
    public static class BuddyTts
    {
        private const int MaxChars = 200;
        private const int MaxQueuedLines = 3;

        private static bool _inFlight;
        private static bool _blockedByModelTerms;
        private static readonly Queue<SpeechRequest> Pending = new Queue<SpeechRequest>();

        private struct SpeechRequest
        {
            public string Text;
            public Vector3 Position;
        }

        /// <summary>
        /// Drop queued-but-unspoken lines and release the queue latch so a lobby change can
        /// never carry stale speech (or a wedged coroutine) into the next session.
        /// </summary>
        public static void ResetSession()
        {
            try
            {
                Pending.Clear();
                _inFlight = false;
                BuddyNetworkAudio.StopPlayback();
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"BuddyTts.ResetSession: {ex.Message}");
            }
        }

        public static void Speak(string text, Vector3 worldPos)
        {
            try
            {
                if (Plugin.TtsEnabled == null || !Plugin.TtsEnabled.Value) return;
                if (!GroqSecrets.HasKey) return;
                if (!GroqSecrets.IsOpenAi && _blockedByModelTerms) return;
                if (string.IsNullOrWhiteSpace(text)) return;
                if (!CrewmateSpawner.IsHost()) return;
                if (Plugin.Host == null) return;
                string cleaned = SanitizeForTts(text);
                if (string.IsNullOrEmpty(cleaned)) return;
                if (cleaned.Length > MaxChars)
                    cleaned = cleaned.Substring(0, MaxChars - 1).TrimEnd() + ".";

                // OpenAI speech is generated natively by the same Realtime model that runs
                // Buddy's conversation and tools. The request-based TTS endpoint is Groq-only.
                if (GroqSecrets.IsOpenAi)
                {
                    OpenAiRealtimeVoiceClient.EnqueueExactSpeech(cleaned);
                    return;
                }

                if (Pending.Count >= MaxQueuedLines)
                {
                    Plugin.Log?.LogInfo("TTS queue full; dropping oldest stale line.");
                    Pending.Dequeue();
                }
                Pending.Enqueue(new SpeechRequest { Text = cleaned, Position = worldPos });
                if (!_inFlight)
                {
                    _inFlight = true;
                    Plugin.Host.StartCoroutine(ProcessQueue());
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"BuddyTts.Speak: {ex}");
            }
        }

        private static string SanitizeForTts(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            text = text.Replace("[FOLLOW]", "").Replace("[STAY]", "")
                .Replace("[SHIP]", "").Replace("[FETCH]", "").Trim();

            // Orpheus accepts bracketed delivery directions. OpenAI native Realtime speech
            // receives plain dialogue and must never inherit Groq-specific prompt syntax.
            if (!GroqSecrets.IsOpenAi && text.IndexOf('[') < 0)
            {
                string dir = Plugin.TtsDirection?.Value ?? "";
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    dir = dir.Trim().Trim('[', ']');
                    string withDir = "[" + dir + "] " + text;
                    if (withDir.Length <= MaxChars)
                        text = withDir;
                }
            }
            return text;
        }

        /// <summary>Groq chat/STT models must never hit /audio/speech — Orpheus only.</summary>
        private static string ResolveTtsModel()
        {
            return BuddyAiArchitecture.GroqSpeechModel;
        }

        private static IEnumerator ProcessQueue()
        {
            try
            {
                while (Pending.Count > 0)
                {
                    SpeechRequest request = Pending.Dequeue();
                    yield return RequestAndPlayCore(request.Text, request.Position);
                    while (BuddyNetworkAudio.IsPlaying)
                        yield return null;
                }
            }
            finally
            {
                _inFlight = false;
            }
        }

        private static IEnumerator RequestAndPlayCore(string input, Vector3 worldPos)
        {
            string model = ResolveTtsModel();
            string voice = Plugin.TtsVoice?.Value ?? "austin";
            if (string.IsNullOrWhiteSpace(voice)) voice = "austin";
            string body = "{\"model\":\"" + LlmClient.Escape(model) +
                          "\",\"voice\":\"" + LlmClient.Escape(voice) +
                          "\",\"input\":\"" + LlmClient.Escape(input) +
                          "\",\"response_format\":\"wav\"}";

            byte[] audioBytes = null;

            using (var uwr = new UnityWebRequest(GroqSecrets.TtsEndpoint, "POST"))
            {
                byte[] raw = Encoding.UTF8.GetBytes(body);
                uwr.uploadHandler = new UploadHandlerRaw(raw);
                uwr.downloadHandler = new DownloadHandlerBuffer();
                uwr.SetRequestHeader("Content-Type", "application/json");
                uwr.SetRequestHeader("Authorization", "Bearer " + GroqSecrets.CurrentKey);
                uwr.timeout = 15;

                Plugin.Log?.LogInfo($"Buddy TTS request started model={model} voice={voice} chars={input.Length}.");
                yield return uwr.SendWebRequest();

                if (!string.IsNullOrEmpty(uwr.error) || uwr.responseCode < 200 || uwr.responseCode >= 300)
                {
                    string response = uwr.downloadHandler?.text ?? "";
                    Plugin.Log?.LogWarning($"{GroqSecrets.ProviderName} TTS HTTP {uwr.responseCode}: {uwr.error} {response}");
                    if (!GroqSecrets.IsOpenAi && response.IndexOf("model_terms_required", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        _blockedByModelTerms = true;
                        try
                        {
                            HUDManager.Instance?.DisplayTip(
                                "Buddy voice needs Groq approval",
                                "Accept the Orpheus model terms in the Groq console, then restart. Buddy text still works.",
                                true,
                                false,
                                "LethalAICrewmateTtsTerms");
                        }
                        catch { }
                    }
                    yield break;
                }
                audioBytes = uwr.downloadHandler?.data;
                Plugin.Log?.LogInfo($"Buddy TTS HTTP {uwr.responseCode} returned {audioBytes?.Length ?? 0} bytes.");
            }

            if (audioBytes == null || audioBytes.Length < 64)
            {
                Plugin.Log?.LogWarning(GroqSecrets.ProviderName + " TTS: empty body");
                yield break;
            }

            if (audioBytes[0] == (byte)'{')
            {
                Plugin.Log?.LogWarning(GroqSecrets.ProviderName + " TTS returned JSON: " + Encoding.UTF8.GetString(audioBytes));
                yield break;
            }

            // Fix streaming WAV (size fields 0xFFFFFFFF) then decode
            byte[] fixedWav = NormalizeWavSizes(audioBytes);
            Plugin.Log?.LogInfo($"Orpheus TTS {audioBytes.Length} bytes → normalized {fixedWav.Length}");

            if (TryParseWav(fixedWav, out int sampleRate, out int channels, out float[] samples)
                && samples != null && samples.Length > 0 && channels > 0)
            {
                int frames = samples.Length / channels;
                var clip = AudioClip.Create("BuddyOrpheus", frames, channels, sampleRate, false);
                clip.SetData(samples, 0);
                Plugin.Log?.LogInfo($"Buddy TTS decoded clip length={clip.length:F2}s samples={clip.samples} rate={clip.frequency} channels={clip.channels}.");
                BuddyNetworkAudio.PlayHostClipAndReplicate(clip, worldPos);
            }
            else
            {
                Plugin.Log?.LogWarning("TTS: PCM parse failed after normalize");
            }

        }

        /// <summary>
        /// Orpheus sends RIFF/data chunk sizes as 0xFFFFFFFF (streaming). Rewrite real sizes.
        /// </summary>
        internal static byte[] NormalizeWavSizes(byte[] data)
        {
            if (data == null || data.Length < 44) return data;
            byte[] copy = (byte[])data.Clone();

            // RIFF chunk size = file length - 8
            WriteInt32(copy, 4, copy.Length - 8);

            int offset = 12;
            while (offset + 8 <= copy.Length)
            {
                string id = Encoding.ASCII.GetString(copy, offset, 4);
                uint rawSize = BitConverter.ToUInt32(copy, offset + 4);
                int payloadStart = offset + 8;

                if (id == "data")
                {
                    // Rest of file is PCM (streaming size is 0xFFFFFFFF)
                    int realSize = copy.Length - payloadStart;
                    if (rawSize == 0xFFFFFFFFu || rawSize > (uint)realSize || rawSize == 0)
                        WriteInt32(copy, offset + 4, realSize);
                    break;
                }

                int size;
                if (rawSize == 0xFFFFFFFFu || payloadStart + (int)rawSize > copy.Length)
                {
                    // Malformed / streaming mid-chunk — can't advance safely
                    if (id == "fmt ")
                    {
                        // Standard PCM fmt is 16 bytes
                        size = 16;
                        WriteInt32(copy, offset + 4, size);
                    }
                    else
                        break;
                }
                else
                    size = (int)rawSize;

                offset = payloadStart + size;
                if ((size & 1) != 0) offset++;
            }

            // Recompute RIFF size after any writes
            WriteInt32(copy, 4, copy.Length - 8);
            return copy;
        }

        private static void WriteInt32(byte[] buf, int o, int v)
        {
            buf[o] = (byte)(v & 0xff);
            buf[o + 1] = (byte)((v >> 8) & 0xff);
            buf[o + 2] = (byte)((v >> 16) & 0xff);
            buf[o + 3] = (byte)((v >> 24) & 0xff);
        }

        internal static bool TryParseWav(byte[] data, out int sampleRate, out int channels, out float[] samples)
        {
            sampleRate = 0;
            channels = 0;
            samples = null;
            if (data == null || data.Length < 44) return false;
            if (data[0] != 'R' || data[1] != 'I' || data[2] != 'F' || data[3] != 'F') return false;
            if (data[8] != 'W' || data[9] != 'A' || data[10] != 'V' || data[11] != 'E') return false;

            int offset = 12;
            int dataOffset = -1;
            int dataSize = 0;
            short bitsPerSample = 16;
            short audioFormat = 1;
            channels = 1;
            sampleRate = 48000;

            while (offset + 8 <= data.Length)
            {
                string id = Encoding.ASCII.GetString(data, offset, 4);
                uint rawSize = BitConverter.ToUInt32(data, offset + 4);
                int payloadStart = offset + 8;
                int remaining = data.Length - payloadStart;

                int size;
                if (rawSize == 0xFFFFFFFFu || rawSize > (uint)remaining)
                    size = remaining;
                else
                    size = (int)rawSize;

                if (id == "fmt " && size >= 16)
                {
                    audioFormat = BitConverter.ToInt16(data, payloadStart);
                    channels = BitConverter.ToInt16(data, payloadStart + 2);
                    sampleRate = BitConverter.ToInt32(data, payloadStart + 4);
                    bitsPerSample = BitConverter.ToInt16(data, payloadStart + 14);
                    if (audioFormat == unchecked((short)0xFFFE) && size >= 40)
                        audioFormat = BitConverter.ToInt16(data, payloadStart + 24);
                }
                else if (id == "data")
                {
                    dataOffset = payloadStart;
                    dataSize = size;
                    break;
                }

                // Advance by declared size when known; else skip padded
                if (rawSize != 0xFFFFFFFFu && rawSize <= (uint)remaining)
                {
                    offset = payloadStart + (int)rawSize;
                    if (((int)rawSize & 1) != 0) offset++;
                }
                else if (id == "fmt ")
                {
                    offset = payloadStart + 16;
                    if (offset < data.Length && data[offset] == 0) { /* pad */ }
                    // find next chunk by scanning for known ids is hard; use 16-byte fmt
                    offset = payloadStart + 16;
                }
                else
                    break;
            }

            // Fallback: search for "data" magic
            if (dataOffset < 0)
            {
                for (int i = 12; i < data.Length - 8; i++)
                {
                    if (data[i] == 'd' && data[i + 1] == 'a' && data[i + 2] == 't' && data[i + 3] == 'a')
                    {
                        dataOffset = i + 8;
                        uint rs = BitConverter.ToUInt32(data, i + 4);
                        dataSize = (rs == 0xFFFFFFFFu || rs > data.Length - dataOffset)
                            ? data.Length - dataOffset
                            : (int)rs;
                        break;
                    }
                }
            }

            if (dataOffset < 0 || dataSize <= 0 || channels <= 0 || sampleRate <= 0)
            {
                Plugin.Log?.LogWarning($"WAV incomplete fmt={audioFormat} bits={bitsPerSample} dataOff={dataOffset} dataSize={dataSize}");
                return false;
            }

            dataSize = Math.Min(dataSize, data.Length - dataOffset);

            try
            {
                if (audioFormat == 1 && bitsPerSample == 16)
                {
                    int sampleCount = dataSize / 2;
                    samples = new float[sampleCount];
                    for (int i = 0; i < sampleCount; i++)
                    {
                        short s = BitConverter.ToInt16(data, dataOffset + i * 2);
                        samples[i] = s / 32768f;
                    }
                    return sampleCount > 0;
                }

                if (audioFormat == 3 && bitsPerSample == 32)
                {
                    int sampleCount = dataSize / 4;
                    samples = new float[sampleCount];
                    for (int i = 0; i < sampleCount; i++)
                        samples[i] = BitConverter.ToSingle(data, dataOffset + i * 4);
                    return sampleCount > 0;
                }

                if (audioFormat == 1 && bitsPerSample == 32)
                {
                    // Sometimes 32-bit int PCM
                    int sampleCount = dataSize / 4;
                    samples = new float[sampleCount];
                    for (int i = 0; i < sampleCount; i++)
                    {
                        int s = BitConverter.ToInt32(data, dataOffset + i * 4);
                        samples[i] = s / 2147483648f;
                    }
                    return sampleCount > 0;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"WAV decode: {ex.Message}");
                return false;
            }

            Plugin.Log?.LogWarning($"Unsupported WAV fmt={audioFormat} bits={bitsPerSample}");
            return false;
        }
    }
}
