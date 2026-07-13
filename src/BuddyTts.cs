using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace LethalAICrewmate
{
    /// <summary>
    /// Groq Orpheus TTS: text → WAV → 3D AudioSource near Buddy (host).
    /// Model: canopylabs/orpheus-v1-english (max 200 chars per request).
    /// </summary>
    public static class BuddyTts
    {
        private const string Endpoint = "https://api.groq.com/openai/v1/audio/speech";
        private const int MaxChars = 200;

        private static bool _inFlight;
        private static AudioSource _source;
        private static GameObject _audioGo;

        public static void Speak(string text, Vector3 worldPos)
        {
            try
            {
                if (Plugin.TtsEnabled == null || !Plugin.TtsEnabled.Value) return;
                if (string.IsNullOrEmpty(Plugin.ApiKey?.Value)) return;
                if (string.IsNullOrWhiteSpace(text)) return;
                if (!CrewmateSpawner.IsHost()) return;
                if (Plugin.Host == null) return;
                if (_inFlight)
                {
                    Plugin.Log?.LogInfo("TTS busy; dropping line.");
                    return;
                }

                string cleaned = SanitizeForTts(text);
                if (string.IsNullOrEmpty(cleaned)) return;

                // Orpheus hard limit: 200 characters
                if (cleaned.Length > MaxChars)
                    cleaned = cleaned.Substring(0, MaxChars - 1).TrimEnd() + "…";

                Plugin.Host.StartCoroutine(RequestAndPlay(cleaned, worldPos));
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"BuddyTts.Speak: {ex}");
            }
        }

        private static string SanitizeForTts(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            // Strip leftover command tags
            text = text.Replace("[FOLLOW]", "").Replace("[STAY]", "")
                .Replace("[SHIP]", "").Replace("[FETCH]", "");
            text = text.Trim();
            // Slight nervous crewmate flavor if no direction already present
            if (text.IndexOf('[') < 0)
            {
                string dir = Plugin.TtsDirection?.Value ?? "";
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    dir = dir.Trim();
                    if (!dir.StartsWith("[")) dir = "[" + dir + "]";
                    // directions count toward 200-char limit
                    string withDir = dir + " " + text;
                    if (withDir.Length <= MaxChars)
                        text = withDir;
                }
            }
            return text;
        }

        private static IEnumerator RequestAndPlay(string input, Vector3 worldPos)
        {
            _inFlight = true;
            string model = Plugin.TtsModel?.Value ?? "canopylabs/orpheus-v1-english";
            string voice = Plugin.TtsVoice?.Value ?? "troy";

            string body = "{\"model\":\"" + LlmClient.Escape(model) +
                          "\",\"voice\":\"" + LlmClient.Escape(voice) +
                          "\",\"input\":\"" + LlmClient.Escape(input) +
                          "\",\"response_format\":\"wav\"}";

            using (var uwr = new UnityWebRequest(Endpoint, "POST"))
            {
                byte[] raw = Encoding.UTF8.GetBytes(body);
                uwr.uploadHandler = new UploadHandlerRaw(raw);
                uwr.downloadHandler = new DownloadHandlerBuffer();
                uwr.SetRequestHeader("Content-Type", "application/json");
                uwr.SetRequestHeader("Authorization", "Bearer " + Plugin.ApiKey.Value);

                Plugin.Log?.LogInfo($"Orpheus TTS → voice={voice} chars={input.Length}");
                yield return uwr.SendWebRequest();

                try
                {
                    bool ok = string.IsNullOrEmpty(uwr.error)
                              && uwr.responseCode >= 200
                              && uwr.responseCode < 300;
                    if (!ok)
                    {
                        Plugin.Log?.LogWarning(
                            $"Orpheus TTS HTTP {uwr.responseCode}: {uwr.error} {uwr.downloadHandler?.text}");
                    }
                    else
                    {
                        byte[] wav = uwr.downloadHandler.data;
                        if (wav != null && wav.Length > 44)
                            PlayWav(wav, worldPos);
                        else
                            Plugin.Log?.LogWarning("Orpheus TTS: empty audio body");
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log?.LogError($"TTS play: {ex}");
                }
            }

            _inFlight = false;
        }

        private static void PlayWav(byte[] wav, Vector3 worldPos)
        {
            if (!TryParseWav(wav, out int sampleRate, out int channels, out float[] samples))
            {
                Plugin.Log?.LogWarning("Orpheus TTS: failed to parse WAV");
                return;
            }

            EnsureAudioSource();

            // Follow Buddy if still around
            var primary = CrewmateRegistry.GetPrimary();
            if (primary?.Enemy != null)
                worldPos = primary.Enemy.transform.position + Vector3.up * 1.6f;

            _audioGo.transform.position = worldPos;

            var clip = AudioClip.Create(
                "BuddyOrpheus",
                samples.Length / channels,
                channels,
                sampleRate,
                false);
            clip.SetData(samples, 0);

            _source.Stop();
            _source.clip = clip;
            _source.spatialBlend = 1f; // 3D
            _source.minDistance = 2f;
            _source.maxDistance = Mathf.Max(8f, Plugin.ChatHearRange?.Value ?? 25f);
            _source.rolloffMode = AudioRolloffMode.Linear;
            _source.volume = Mathf.Clamp01(Plugin.TtsVolume?.Value ?? 0.85f);
            _source.Play();

            Plugin.Log?.LogInfo($"Playing Buddy TTS ({samples.Length / channels} samples @ {sampleRate}Hz)");
        }

        private static void EnsureAudioSource()
        {
            if (_audioGo != null && _source != null) return;
            _audioGo = new GameObject("LethalAICrewmate_TTS");
            UnityEngine.Object.DontDestroyOnLoad(_audioGo);
            _source = _audioGo.AddComponent<AudioSource>();
            _source.playOnAwake = false;
            _source.loop = false;
            _source.spatialize = false; // built-in 3D attenuation without spatializer plugin
        }

        /// <summary>Minimal PCM WAV parser (16-bit PCM, mono/stereo).</summary>
        internal static bool TryParseWav(byte[] data, out int sampleRate, out int channels, out float[] samples)
        {
            sampleRate = 0;
            channels = 0;
            samples = null;
            if (data == null || data.Length < 44) return false;

            // RIFF....WAVE
            if (data[0] != 'R' || data[1] != 'I' || data[2] != 'F' || data[3] != 'F') return false;
            if (data[8] != 'W' || data[9] != 'A' || data[10] != 'V' || data[11] != 'E') return false;

            int offset = 12;
            int dataOffset = -1;
            int dataSize = 0;
            short bitsPerSample = 16;
            channels = 1;
            sampleRate = 48000;

            while (offset + 8 <= data.Length)
            {
                string id = Encoding.ASCII.GetString(data, offset, 4);
                int size = BitConverter.ToInt32(data, offset + 4);
                offset += 8;
                if (offset + size > data.Length) break;

                if (id == "fmt ")
                {
                    short audioFormat = BitConverter.ToInt16(data, offset);
                    channels = BitConverter.ToInt16(data, offset + 2);
                    sampleRate = BitConverter.ToInt32(data, offset + 4);
                    bitsPerSample = BitConverter.ToInt16(data, offset + 14);
                    if (audioFormat != 1)
                    {
                        Plugin.Log?.LogWarning($"WAV format {audioFormat} not PCM");
                        return false;
                    }
                }
                else if (id == "data")
                {
                    dataOffset = offset;
                    dataSize = size;
                    break;
                }

                offset += size;
                if ((size & 1) != 0) offset++; // word align
            }

            if (dataOffset < 0 || dataSize <= 0) return false;
            if (bitsPerSample != 16)
            {
                Plugin.Log?.LogWarning($"WAV bits {bitsPerSample} unsupported (need 16)");
                return false;
            }

            int sampleCount = dataSize / 2;
            samples = new float[sampleCount];
            for (int i = 0; i < sampleCount; i++)
            {
                short s = BitConverter.ToInt16(data, dataOffset + i * 2);
                samples[i] = s / 32768f;
            }
            return samples.Length > 0 && channels > 0 && sampleRate > 0;
        }
    }
}
