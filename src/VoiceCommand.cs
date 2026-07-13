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

        private static bool _recording;
        private static string _micDevice;
        private static AudioClip _clip;
        private static float _startedAt;
        private static bool _busy;
        private static float _hintCooldown;

        public static void Tick()
        {
            try
            {
                if (Plugin.VoiceEnabled == null || !Plugin.VoiceEnabled.Value) return;
                if (!CrewmateSpawner.IsHost()) return;
                if (string.IsNullOrEmpty(Plugin.ApiKey?.Value)) return;

                // Don't steal typing in chat/terminal
                if (IsTextInputFocused()) return;

                var key = Plugin.VoiceKey?.Value ?? KeyCode.V;
                float maxSec = Mathf.Clamp(Plugin.VoiceMaxSeconds?.Value ?? 8f, 1f, 15f);

                if (!_recording && !_busy && Input.GetKeyDown(key))
                    BeginRecord(maxSec);
                else if (_recording && (Input.GetKeyUp(key) || Time.unscaledTime - _startedAt >= maxSec))
                    EndRecordAndSend();
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"VoiceCommand.Tick: {ex}");
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

        private static void BeginRecord(float maxSec)
        {
            try
            {
                if (Microphone.devices == null || Microphone.devices.Length == 0)
                {
                    MaybeHint("No microphone found for Buddy voice.");
                    return;
                }

                _micDevice = Microphone.devices[0];
                _clip = Microphone.Start(_micDevice, false, Mathf.CeilToInt(maxSec) + 1, SampleRate);
                if (_clip == null)
                {
                    MaybeHint("Microphone failed to start.");
                    return;
                }

                _recording = true;
                _startedAt = Time.unscaledTime;
                MaybeHint("Listening for Buddy… (release key)");
                Plugin.Log?.LogInfo($"Voice PTT start device='{_micDevice}'");
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
                int pos = Microphone.GetPosition(_micDevice);
                Microphone.End(_micDevice);

                if (_clip == null || pos < SampleRate / 4) // <0.25s
                {
                    Plugin.Log?.LogInfo("Voice clip too short; discarded.");
                    return;
                }

                if (Plugin.Host == null) return;
                _busy = true;
                Plugin.Host.StartCoroutine(TranscribeAndHandle(_clip, pos));
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"EndRecordAndSend: {ex}");
                _busy = false;
            }
        }

        private static IEnumerator TranscribeAndHandle(AudioClip clip, int samplePos)
        {
            byte[] wav = null;
            try
            {
                wav = ClipToWav(clip, samplePos);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"ClipToWav: {ex}");
                _busy = false;
                yield break;
            }

            if (wav == null || wav.Length < 100)
            {
                _busy = false;
                yield break;
            }

            string model = Plugin.SttModel?.Value ?? "whisper-large-v3-turbo";
            string boundary = "----LethalAIBuddy" + UnityEngine.Random.Range(100000, 999999);

            byte[] body = BuildMultipart(boundary, wav, model);

            using (var uwr = new UnityWebRequest(GroqSttEndpoint, "POST"))
            {
                uwr.uploadHandler = new UploadHandlerRaw(body);
                uwr.downloadHandler = new DownloadHandlerBuffer();
                uwr.SetRequestHeader("Authorization", "Bearer " + Plugin.ApiKey.Value);
                uwr.SetRequestHeader("Content-Type", "multipart/form-data; boundary=" + boundary);

                Plugin.Log?.LogInfo($"Groq STT → model={model} bytes={wav.Length}");
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

        private static void HandleTranscript(string text)
        {
            // Show what you said in chat (local feedback)
            try
            {
                string you = "You (voice)";
                if (HUDManager.Instance != null)
                    HUDManager.Instance.AddChatMessage(text, you);
            }
            catch { /* ignore */ }

            // Ensure Buddy is addressed so commands + LLM always fire
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
            // {"text":"..."}
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
            // multipart/form-data with file + model + response_format
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

        private static byte[] ClipToWav(AudioClip clip, int samplePos)
        {
            int channels = clip.channels;
            // samplePos is in samples per Microphone docs (not frames*channels for single channel usually)
            int samples = Mathf.Clamp(samplePos, 0, clip.samples);
            if (samples <= 0) samples = clip.samples;

            float[] data = new float[samples * channels];
            clip.GetData(data, 0);

            // Convert to 16-bit mono PCM
            short[] pcm = new short[samples];
            for (int i = 0; i < samples; i++)
            {
                float sum = 0f;
                for (int c = 0; c < channels; c++)
                    sum += data[i * channels + c];
                float s = sum / channels;
                s = Mathf.Clamp(s, -1f, 1f);
                pcm[i] = (short)(s * short.MaxValue);
            }

            int byteRate = SampleRate * 2; // mono 16-bit
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
            wi16(20, 1); // PCM
            wi16(22, 1); // mono
            wi32(24, SampleRate);
            wi32(28, byteRate);
            wi16(32, 2); // block align
            wi16(34, 16); // bits
            wstr(36, "data");
            wi32(40, dataLen);

            for (int i = 0; i < pcm.Length; i++)
                wi16(44 + i * 2, pcm[i]);

            return wav;
        }

        private static void MaybeHint(string msg)
        {
            if (Time.unscaledTime < _hintCooldown) return;
            _hintCooldown = Time.unscaledTime + 2f;
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
