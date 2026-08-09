using System;
using System.Collections;
using UnityEngine;

namespace LethalAICrewmate
{
    /// <summary>
    /// Host push-to-talk capture for the single gpt-realtime-2.1-mini speech-to-speech path.
    /// Hold Voice.PushToTalkKey (default B), then release to send.
    /// </summary>
    public static class VoiceCommand
    {
        private const int SampleRate = 16000;
        private static bool _recording;
        private static string _micDevice;
        private static AudioClip _clip;
        private static float _startedAt;
        private static bool _busy;
        private static float _hintCooldown;
        private static float _lastPttTime;
        private static KeyCode _recordingKey;

        public static void Tick()
        {
            try
            {
                if (Plugin.VoiceEnabled?.Value != true || !CrewmateSpawner.IsHost() ||
                    !CrewmateSpawner.CanTalkToBuddy || !OpenAiSecrets.HasKey || _busy || IsTextInputFocused()) return;

                KeyCode primary = Plugin.VoiceKey?.Value ?? KeyCode.B;
                KeyCode alternate = Plugin.VoiceAlternateKey?.Value ?? KeyCode.None;
                float maxSec = Mathf.Clamp(Plugin.VoiceMaxSeconds?.Value ?? 6f, 1f, 12f);
                if (!_recording && (InputCompat.GetKeyDown(primary) ||
                    (alternate != KeyCode.None && alternate != primary && InputCompat.GetKeyDown(alternate))))
                {
                    if (Time.unscaledTime - _lastPttTime < 0.35f) return;
                    _recordingKey = InputCompat.GetKeyDown(primary) ? primary : alternate;
                    BeginRecord(maxSec);
                }
                else if (_recording && (InputCompat.GetKeyUp(_recordingKey) || Time.unscaledTime - _startedAt >= maxSec))
                {
                    _lastPttTime = Time.unscaledTime;
                    EndRecordAndSend();
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError("VoiceCommand.Tick: " + ex.Message);
                _recording = false;
                _busy = false;
            }
        }

        private static bool IsTextInputFocused()
        {
            try { return HUDManager.Instance?.chatTextField?.isFocused == true; }
            catch { return false; }
        }

        private static void BeginRecord(float maxSec)
        {
            try
            {
                LlmClient.NotePlayerInteraction();
                OpenAiRealtimeVoiceClient.BeginPushToTalk();
                try
                {
                    if (!string.IsNullOrEmpty(_micDevice) || _clip != null) Microphone.End(_micDevice);
                }
                catch { }

                _micDevice = MicrophoneCapture.ResolveConfiguredDevice();
                VoiceCoexistence.BeginBuddyCapture(_micDevice);
                int length = Mathf.Clamp(Mathf.CeilToInt(maxSec) + 1, 2, 13);
                _clip = Microphone.Start(_micDevice, false, length, SampleRate);
                if (_clip == null)
                {
                    VoiceCoexistence.EndBuddyCapture();
                    MaybeHint("Microphone failed to start.");
                    return;
                }
                _recording = true;
                _startedAt = Time.unscaledTime;
                Plugin.Log?.LogInfo("Voice PTT recording started.");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError("BeginRecord: " + ex);
                _recording = false;
            }
        }

        private static void EndRecordAndSend()
        {
            if (!_recording) return;
            _recording = false;
            try
            {
                float waited = 0f;
                int position = 0;
                while (waited < 0.15f)
                {
                    position = Microphone.GetPosition(_micDevice);
                    if (position > SampleRate / 10) break;
                    waited += 0.02f;
                }
                position = Microphone.GetPosition(_micDevice);
                Microphone.End(_micDevice);
                VoiceCoexistence.EndBuddyCapture();
                float duration = Time.unscaledTime - _startedAt;
                if (_clip == null || position < SampleRate / 5 || duration < 0.35f)
                {
                    Plugin.Log?.LogInfo("Voice clip too short; discarded.");
                    return;
                }
                if (Plugin.Host == null) return;
                _busy = true;
                Plugin.Host.StartCoroutine(SendRealtime(_clip, position));
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError("EndRecordAndSend: " + ex);
                _busy = false;
            }
        }

        private static IEnumerator SendRealtime(AudioClip clip, int samplePosition)
        {
            yield return null;
            byte[] wav;
            try
            {
                wav = MicrophoneCapture.EncodeAdaptiveMonoWav(
                    clip, samplePosition, out float inputRms, out float outputRms, out float gain);
                if (wav == null || wav.Length < 1000)
                {
                    _busy = false;
                    yield break;
                }
                if (!VoiceSignalMath.HasUsableSignal(inputRms))
                {
                    MaybeHint("Buddy heard silence. Set Voice.InputDevice if Windows chose the wrong mic.");
                    _busy = false;
                    yield break;
                }

                int playerId = 0;
                string playerName = "Player";
                var local = StartOfRound.Instance?.localPlayerController;
                if (local != null)
                {
                    playerId = (int)local.playerClientId;
                    playerName = local.playerUsername ?? "Player";
                }
                if (!OpenAiRealtimeVoiceClient.EnqueueWav(wav, playerId, playerName))
                    MaybeHint("Buddy couldn't start the Realtime turn. Try again.");
                else
                    Plugin.Log?.LogInfo("Queued native Realtime voice turn bytes=" + wav.Length +
                        " inputRms=" + inputRms.ToString("F5") + " outputRms=" + outputRms.ToString("F4") +
                        " gain=" + gain.ToString("F1") + ".");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError("Realtime microphone send: " + ex);
            }
            _busy = false;
        }

        private static void MaybeHint(string message)
        {
            if (Time.unscaledTime < _hintCooldown) return;
            _hintCooldown = Time.unscaledTime + 3f;
            try { HUDManager.Instance?.DisplayTip("Buddy", message, false, false, "BuddyTip"); }
            catch { Plugin.Log?.LogInfo(message); }
        }
    }
}
