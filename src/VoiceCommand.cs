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
    /// Hold Voice.PushToTalkKey (default B), release to send.
    /// </summary>
    public static class VoiceCommand
    {
        private const string GroqSttEndpoint = "https://api.groq.com/openai/v1/audio/transcriptions";
        private const int SampleRate = 16000;

        private static bool _recording;
        private static string _micDevice;
        private static AudioClip _clip;
        private static float _startedAt;
        private static bool _busy;
        private static float _hintCooldown;
        private static float _lastPttTime;

        public static void Tick()
        {
            try
            {
                if (Plugin.VoiceEnabled == null || !Plugin.VoiceEnabled.Value) return;
                if (!CrewmateSpawner.IsHost()) return;
                if (!GroqSecrets.HasKey) return;
                if (_busy) return;

                if (IsTextInputFocused()) return;

                var key = Plugin.VoiceKey?.Value ?? KeyCode.B;
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

                _micDevice = MicrophoneCapture.ResolveConfiguredDevice();
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
            float inputRms = 0f;
            float outputRms = 0f;
            float gain = 1f;
            try
            {
                wav = MicrophoneCapture.EncodeAdaptiveMonoWav(
                    clip, samplePos, out inputRms, out outputRms, out gain);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"Microphone encode: {ex}");
                _busy = false;
                yield break;
            }

            if (wav == null || wav.Length < 1000)
            {
                Plugin.Log?.LogInfo("WAV empty; discarded.");
                _busy = false;
                yield break;
            }

            if (!VoiceSignalMath.HasUsableSignal(inputRms))
            {
                Plugin.Log?.LogWarning($"Mic contains no usable signal (input rms={inputRms:F5}). STT skipped.");
                MaybeHint("Buddy heard silence. Set Voice.InputDevice if Windows chose the wrong mic.");
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

                Plugin.Log?.LogInfo($"Groq STT → model={model} bytes={wav.Length} inputRms={inputRms:F5} outputRms={outputRms:F4} gain={gain:F1}");
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
            part("prompt", "Lethal Company gameplay. Crew talking to Buddy AI. Commands: follow, stay, ship, fetch scrap, go forward, scout ahead, check in front, status, time, credits, buy items, open door, disable turret, ship lights.");

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
