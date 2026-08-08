using System;
using System.Collections;
using System.Text;
using GameNetcodeStuff;
using UnityEngine;
using UnityEngine.Networking;

namespace LethalAICrewmate
{
    /// <summary>
    /// Host push-to-talk → Groq Whisper STT → Buddy chat/commands.
    /// Hold Voice.PushToTalkKey (default V), release to send.
    /// </summary>
    public static class VoiceCommand
    {
        private const string GroqSttEndpoint = "https://api.groq.com/openai/v1/audio/transcriptions";
        private const int SampleRate = 16000;
        private const float MinRms = 0.008f; // below this = silence / wrong mic

        private static bool _recording;
        private static string _micDevice;
        private static AudioClip _clip;
        private static float _startedAt;
        private static bool _busy;
        private static float _hintCooldown;
        private static float _lastPttTime;
        private static string _cachedMic;
        private static bool _micLogged;

        public static void Tick()
        {
            try
            {
                if (Plugin.VoiceEnabled == null || !Plugin.VoiceEnabled.Value) return;
                if (!CrewmateSpawner.IsHost()) return;
                if (!GroqSecrets.HasKey) return;
                if (_busy) return;

                if (IsTextInputFocused()) return;

                var key = Plugin.VoiceKey?.Value ?? KeyCode.V;
                float maxSec = Mathf.Clamp(Plugin.VoiceMaxSeconds?.Value ?? 6f, 1f, 12f);

                if (!_recording && InputCompat.GetKeyDown(key))
                {
                    // Debounce accidental double-taps
                    if (Time.unscaledTime - _lastPttTime < 0.35f) return;
                    BeginRecord(maxSec);
                }
                else if (_recording && (InputCompat.GetKeyUp(key) || Time.unscaledTime - _startedAt >= maxSec))
                {
                    _lastPttTime = Time.unscaledTime;
                    EndRecordAndSend();
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"VoiceCommand.Tick: {ex.Message}");
                _recording = false;
                _busy = false;
            }
        }

        private static bool IsTextInputFocused()
        {
            try
            {
                var hud = HUDManager.Instance;
                if (hud?.chatTextField != null && hud.chatTextField.isFocused)
                    return true;
            }
            catch { /* ignore */ }
            return false;
        }

        /// <summary>
        /// Chat model (qwen/llama) must never hit /audio/transcriptions.
        /// </summary>
        private static string ResolveSttModel()
        {
            string m = Plugin.SttModel?.Value;
            if (string.IsNullOrWhiteSpace(m) ||
                m.IndexOf("whisper", StringComparison.OrdinalIgnoreCase) < 0)
            {
                if (!string.IsNullOrWhiteSpace(m))
                    Plugin.Log?.LogWarning($"SttModel '{m}' is not Whisper; using whisper-large-v3-turbo");
                return "whisper-large-v3-turbo";
            }
            return m.Trim();
        }

        /// <summary>Pick a real capture device — skip Oculus/VB-Cable virtual mics that capture silence.</summary>
        private static string PickMicDevice()
        {
            if (!string.IsNullOrEmpty(_cachedMic))
                return _cachedMic == "__default__" ? null : _cachedMic;

            string[] devices = Microphone.devices;
            if (devices == null || devices.Length == 0)
            {
                _cachedMic = "__default__";
                return null;
            }

            string best = null;
            foreach (var d in devices)
            {
                if (string.IsNullOrEmpty(d)) continue;
                string lower = d.ToLowerInvariant();
                // Skip known garbage virtual devices (cause Whisper "Thank you" on silence)
                if (lower.Contains("oculus") || lower.Contains("virtual") || lower.Contains("cable") ||
                    lower.Contains("stereo mix") || lower.Contains("what u hear") ||
                    lower.Contains("mapper") || lower.Contains("steam streaming"))
                {
                    Plugin.Log?.LogInfo($"Skipping virtual mic: '{d}'");
                    continue;
                }
                // Prefer names that look like real headsets / mics
                if (best == null) best = d;
                if (lower.Contains("mic") || lower.Contains("headset") || lower.Contains("realtek") ||
                    lower.Contains("logitech") || lower.Contains("hyperx") || lower.Contains("steelseries") ||
                    lower.Contains("usb") || lower.Contains("array"))
                {
                    best = d;
                    break;
                }
            }

            if (best == null)
            {
                // Unity null = system default (usually better than first virtual entry)
                Plugin.Log?.LogWarning("No non-virtual mic found; using system default (null).");
                _cachedMic = "__default__";
                return null;
            }

            _cachedMic = best;
            if (!_micLogged)
            {
                _micLogged = true;
                Plugin.Log?.LogInfo($"Buddy voice mic: '{best}' (of {devices.Length} devices)");
            }
            return best;
        }

        private static void BeginRecord(float maxSec)
        {
            try
            {
                // End any leftover session first
                try
                {
                    if (!string.IsNullOrEmpty(_micDevice) || _clip != null)
                        Microphone.End(_micDevice);
                }
                catch { /* ignore */ }

                // Unity's null device follows the Windows default recording device. Guessing from
                // device names frequently selected a disconnected webcam/headset microphone.
                _micDevice = null;
                int len = Mathf.Clamp(Mathf.CeilToInt(maxSec) + 1, 2, 13);
                _clip = Microphone.Start(_micDevice, false, len, SampleRate);
                if (_clip == null)
                {
                    MaybeHint("Microphone failed to start.");
                    return;
                }

                _recording = true;
                _startedAt = Time.unscaledTime;
                // No DisplayTip here — it hitches the game every PTT
                Plugin.Log?.LogInfo($"Voice PTT start device='{_micDevice ?? "default"}'");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"BeginRecord: {ex}");
                _recording = false;
            }
        }

        private static void EndRecordAndSend()
        {
            if (!_recording) return;
            _recording = false;

            try
            {
                // Wait until mic has written something
                float waited = 0f;
                int pos = 0;
                while (waited < 0.15f)
                {
                    pos = Microphone.GetPosition(_micDevice);
                    if (pos > SampleRate / 10) break;
                    waited += 0.02f;
                }

                pos = Microphone.GetPosition(_micDevice);
                Microphone.End(_micDevice);

                float duration = Time.unscaledTime - _startedAt;
                if (_clip == null || pos < SampleRate / 5 || duration < 0.35f)
                {
                    Plugin.Log?.LogInfo($"Voice clip too short (pos={pos}, {duration:F2}s); discarded.");
                    return;
                }

                if (Plugin.Host == null) return;
                _busy = true;
                // Capture fields before coroutine (clip may be reused)
                var clip = _clip;
                int samplePos = pos;
                Plugin.Host.StartCoroutine(TranscribeAndHandle(clip, samplePos));
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"EndRecordAndSend: {ex}");
                _busy = false;
            }
        }

        private static IEnumerator TranscribeAndHandle(AudioClip clip, int samplePos)
        {
            // Spread work across frames to avoid hitch
            yield return null;

            byte[] wav = null;
            float rms = 0f;
            try
            {
                wav = ClipToWav(clip, samplePos, out rms);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"ClipToWav: {ex}");
                _busy = false;
                yield break;
            }

            if (wav == null || wav.Length < 1000)
            {
                Plugin.Log?.LogInfo("WAV empty; discarded.");
                _busy = false;
                yield break;
            }

            if (rms < MinRms)
            {
                Plugin.Log?.LogWarning($"Mic too quiet (rms={rms:F4}). Wrong device or muted. STT skipped.");
                MaybeHint("Mic too quiet — check Windows input device.");
                _busy = false;
                yield break;
            }

            yield return null;

            // NEVER use the chat model for STT — Whisper only
            string model = ResolveSttModel();
            string boundary = "----LethalAIBuddy" + UnityEngine.Random.Range(100000, 999999);
            byte[] body = BuildMultipart(boundary, wav, model);

            using (var uwr = new UnityWebRequest(GroqSttEndpoint, "POST"))
            {
                uwr.uploadHandler = new UploadHandlerRaw(body);
                uwr.downloadHandler = new DownloadHandlerBuffer();
                uwr.SetRequestHeader("Authorization", "Bearer " + GroqSecrets.CurrentKey);
                uwr.SetRequestHeader("Content-Type", "multipart/form-data; boundary=" + boundary);
                uwr.timeout = 20;

                Plugin.Log?.LogInfo($"Groq STT → model={model} bytes={wav.Length} rms={rms:F4}");
                yield return uwr.SendWebRequest();

                try
                {
                    bool ok = string.IsNullOrEmpty(uwr.error)
                              && uwr.responseCode >= 200
                              && uwr.responseCode < 300;
                    if (!ok)
                    {
                        Plugin.Log?.LogWarning($"Groq STT HTTP {uwr.responseCode}: {uwr.error} {uwr.downloadHandler?.text}");
                        MaybeHint("Buddy couldn't hear you (STT error).");
                    }
                    else
                    {
                        string text = ParseTranscription(uwr.downloadHandler?.text);
                        if (string.IsNullOrWhiteSpace(text))
                        {
                            MaybeHint("Buddy heard silence.");
                        }
                        else if (IsWhisperHallucination(text))
                        {
                            Plugin.Log?.LogWarning($"Ignoring Whisper hallucination: '{text}'");
                            MaybeHint("Didn't catch that — try again closer to mic.");
                        }
                        else
                        {
                            Plugin.Log?.LogInfo($"STT: {text}");
                            HandleTranscript(text.Trim());
                        }
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log?.LogError($"STT handle: {ex}");
                }
            }

            _busy = false;
        }

        /// <summary>Whisper often invents these on silence / bad virtual mics.</summary>
        private static bool IsWhisperHallucination(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return true;
            string t = text.Trim().ToLowerInvariant().TrimEnd('.', '!', '?', ' ');
            string[] bad =
            {
                "thank you", "thanks", "thanks for watching", "thank you for watching",
                "subscribe", "please subscribe", "bye", "goodbye", "you",
                "thank you very much", "thanks for listening", "amen",
                "mbc 뉴스", "null", ".", "..."
            };
            foreach (var b in bad)
                if (t == b) return true;
            // Very short non-command junk
            if (t.Length <= 2) return true;
            return false;
        }

        private static void HandleTranscript(string text)
        {
            try
            {
                if (HUDManager.Instance != null)
                    HUDManager.Instance.AddChatMessage(text, "You (voice)");
            }
            catch { /* ignore */ }

            string name = Plugin.CrewmateName?.Value ?? "Buddy";
            string lower = text.ToLowerInvariant();
            string msg = text;
            if (!lower.Contains(name.ToLowerInvariant()) && !lower.Contains("buddy"))
                msg = name + " " + text;

            int playerId = 0;
            try
            {
                var local = StartOfRound.Instance?.localPlayerController;
                if (local != null)
                    playerId = (int)local.playerClientId;
            }
            catch { /* ignore */ }

            ChatObserver.OnServerChat(msg, playerId);
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
                    switch (n)
                    {
                        case '"': sb.Append('"'); break;
                        case 'n': sb.Append(' '); break;
                        case '\\': sb.Append('\\'); break;
                        default: sb.Append(n); break;
                    }
                }
                else if (c == '"') break;
                else sb.Append(c);
            }
            return sb.ToString().Trim();
        }

        private static byte[] BuildMultipart(string boundary, byte[] wav, string model)
        {
            var sb = new StringBuilder();
            void part(string name, string value)
            {
                sb.Append("--").Append(boundary).Append("\r\n");
                sb.Append("Content-Disposition: form-data; name=\"").Append(name).Append("\"\r\n\r\n");
                sb.Append(value).Append("\r\n");
            }
            part("model", model);
            part("response_format", "json");
            part("language", "en");
            part("temperature", "0");
            // Nudge model away from silence hallucinations
            part("prompt", "Lethal Company gameplay. Crew talking to Buddy AI. Commands: follow stay ship fetch scrap.");

            var head = Encoding.UTF8.GetBytes(sb.ToString());
            var fileHeader = Encoding.UTF8.GetBytes(
                "--" + boundary + "\r\n" +
                "Content-Disposition: form-data; name=\"file\"; filename=\"buddy.wav\"\r\n" +
                "Content-Type: audio/wav\r\n\r\n");
            var mid = Encoding.UTF8.GetBytes("\r\n");
            var end = Encoding.UTF8.GetBytes("--" + boundary + "--\r\n");

            byte[] all = new byte[head.Length + fileHeader.Length + wav.Length + mid.Length + end.Length];
            int o = 0;
            Buffer.BlockCopy(head, 0, all, o, head.Length); o += head.Length;
            Buffer.BlockCopy(fileHeader, 0, all, o, fileHeader.Length); o += fileHeader.Length;
            Buffer.BlockCopy(wav, 0, all, o, wav.Length); o += wav.Length;
            Buffer.BlockCopy(mid, 0, all, o, mid.Length); o += mid.Length;
            Buffer.BlockCopy(end, 0, all, o, end.Length);
            return all;
        }

        private static byte[] ClipToWav(AudioClip clip, int samplePos, out float rms)
        {
            rms = 0f;
            int channels = Mathf.Max(1, clip.channels);
            int samples = Mathf.Clamp(samplePos, 0, clip.samples);
            if (samples <= 0) samples = clip.samples;

            float[] data = new float[samples * channels];
            if (!clip.GetData(data, 0))
            {
                Plugin.Log?.LogWarning("GetData failed on mic clip");
                return null;
            }

            // Use actual clip frequency in header if it differs
            int rate = clip.frequency > 0 ? clip.frequency : SampleRate;

            short[] pcm = new short[samples];
            double sumSq = 0;
            for (int i = 0; i < samples; i++)
            {
                float sum = 0f;
                for (int c = 0; c < channels; c++)
                    sum += data[i * channels + c];
                float s = sum / channels;
                // Soft gain — many headset mics are quiet
                s = Mathf.Clamp(s * 2.2f, -1f, 1f);
                pcm[i] = (short)(s * short.MaxValue);
                sumSq += s * s;
            }
            rms = (float)Math.Sqrt(sumSq / Math.Max(1, samples));

            int byteRate = rate * 2;
            int dataLen = pcm.Length * 2;
            byte[] wav = new byte[44 + dataLen];

            void wstr(int o, string s)
            {
                var b = Encoding.ASCII.GetBytes(s);
                Buffer.BlockCopy(b, 0, wav, o, b.Length);
            }
            void wi32(int o, int v)
            {
                wav[o] = (byte)(v & 0xff);
                wav[o + 1] = (byte)((v >> 8) & 0xff);
                wav[o + 2] = (byte)((v >> 16) & 0xff);
                wav[o + 3] = (byte)((v >> 24) & 0xff);
            }
            void wi16(int o, short v)
            {
                wav[o] = (byte)(v & 0xff);
                wav[o + 1] = (byte)((v >> 8) & 0xff);
            }

            wstr(0, "RIFF");
            wi32(4, 36 + dataLen);
            wstr(8, "WAVE");
            wstr(12, "fmt ");
            wi32(16, 16);
            wi16(20, 1);
            wi16(22, 1);
            wi32(24, rate);
            wi32(28, byteRate);
            wi16(32, 2);
            wi16(34, 16);
            wstr(36, "data");
            wi32(40, dataLen);
            for (int i = 0; i < pcm.Length; i++)
                wi16(44 + i * 2, pcm[i]);

            return wav;
        }

        private static void MaybeHint(string msg)
        {
            if (Time.unscaledTime < _hintCooldown) return;
            _hintCooldown = Time.unscaledTime + 3f;
            try
            {
                if (HUDManager.Instance != null)
                    HUDManager.Instance.DisplayTip("Buddy", msg, false, false, "BuddyTip");
            }
            catch
            {
                Plugin.Log?.LogInfo(msg);
            }
        }
    }
}
