using System;
using UnityEngine;

namespace LethalAICrewmate
{
    /// <summary>
    /// Host push-to-talk capture for the single gpt-realtime-2.1-mini speech-to-speech path.
    /// Audio is streamed to the persistent host Realtime session while the key is held; releasing
    /// the key only commits the already-uploaded input buffer and starts the response.
    /// </summary>
    public static class VoiceCommand
    {
        private const int RequestedSampleRate = 16000;
        private static bool _recording;
        private static string _micDevice;
        private static AudioClip _clip;
        private static float _startedAt;
        private static float _hintCooldown;
        private static float _lastPttTime;
        private static KeyCode _recordingKey;
        private static ulong _streamId;
        private static int _lastSampleFrame;
        private static float _streamGain;
        private static double _inputSquares;
        private static long _inputFrames;

        public static void Tick()
        {
            try
            {
                if (_recording)
                {
                    if (Plugin.VoiceEnabled?.Value != true || !CrewmateSpawner.IsHost() ||
                        !CrewmateSpawner.CanTalkToBuddy || !OpenAiSecrets.HasKey)
                    {
                        AbortRecord("Voice capture stopped because Buddy is no longer available.");
                        return;
                    }

                    FlushStreamingAudio(false);
                    if (InputCompat.GetKeyUp(_recordingKey) ||
                        Time.unscaledTime - _startedAt >= Mathf.Clamp(Plugin.VoiceMaxSeconds?.Value ?? 6f, 1f, 12f))
                    {
                        _lastPttTime = Time.unscaledTime;
                        EndRecordAndCommit();
                    }
                    return;
                }

                if (Plugin.VoiceEnabled?.Value != true || !CrewmateSpawner.IsHost() ||
                    !CrewmateSpawner.CanTalkToBuddy || !OpenAiSecrets.HasKey || IsTextInputFocused()) return;

                KeyCode primary = Plugin.VoiceKey?.Value ?? KeyCode.B;
                KeyCode alternate = Plugin.VoiceAlternateKey?.Value ?? KeyCode.None;
                if (!(InputCompat.GetKeyDown(primary) ||
                    (alternate != KeyCode.None && alternate != primary && InputCompat.GetKeyDown(alternate)))) return;
                if (Time.unscaledTime - _lastPttTime < 0.35f) return;

                _recordingKey = InputCompat.GetKeyDown(primary) ? primary : alternate;
                BeginRecord(Mathf.Clamp(Plugin.VoiceMaxSeconds?.Value ?? 6f, 1f, 12f));
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError("VoiceCommand.Tick: " + ex.Message);
                AbortRecord(null);
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

                int playerId = 0;
                string playerName = "Player";
                var local = StartOfRound.Instance?.localPlayerController;
                if (local != null)
                {
                    playerId = (int)local.playerClientId;
                    playerName = local.playerUsername ?? "Player";
                }

                if (!OpenAiRealtimeVoiceClient.TryBeginStreamingVoice(playerId, playerName, out _streamId))
                {
                    MaybeHint("Buddy is already listening to someone else.");
                    return;
                }

                try
                {
                    if (!string.IsNullOrEmpty(_micDevice) || _clip != null) Microphone.End(_micDevice);
                }
                catch { }

                _micDevice = MicrophoneCapture.ResolveConfiguredDevice();
                VoiceCoexistence.BeginBuddyCapture(_micDevice);
                int length = Mathf.Clamp(Mathf.CeilToInt(maxSec) + 1, 2, 13);
                _clip = Microphone.Start(_micDevice, false, length, RequestedSampleRate);
                if (_clip == null)
                {
                    VoiceCoexistence.EndBuddyCapture();
                    OpenAiRealtimeVoiceClient.AbortStreamingVoice(_streamId);
                    _streamId = 0;
                    MaybeHint("Microphone failed to start.");
                    return;
                }

                _recording = true;
                _startedAt = Time.unscaledTime;
                _lastSampleFrame = 0;
                _streamGain = 0f;
                _inputSquares = 0d;
                _inputFrames = 0;
                Plugin.Log?.LogInfo("Voice PTT streaming started.");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError("BeginRecord: " + ex);
                AbortRecord(null);
            }
        }

        private static void FlushStreamingAudio(bool flushTail)
        {
            if (!_recording || _clip == null || _streamId == 0) return;
            int position = Microphone.GetPosition(_micDevice);
            if (position < 0) return;
            if (position < _lastSampleFrame)
                throw new InvalidOperationException("Microphone position wrapped during non-looping Buddy capture.");

            int available = position - _lastSampleFrame;
            int preferred = StreamingMicCapture.RecommendedSourceFrames(_clip);
            while (available >= preferred || (flushTail && available >= 2))
            {
                int frames = available >= preferred ? preferred : available;
                byte[] pcm = StreamingMicCapture.EncodeChunk(
                    _clip, _lastSampleFrame, frames, ref _streamGain,
                    out float inputRms, out float outputRms);
                _lastSampleFrame += frames;
                available -= frames;
                if (pcm == null || pcm.Length < 4) continue;

                _inputSquares += inputRms * inputRms * frames;
                _inputFrames += frames;
                if (!OpenAiRealtimeVoiceClient.AppendStreamingVoice(_streamId, pcm))
                    throw new InvalidOperationException("Realtime input stream stopped accepting microphone audio.");

                Plugin.Log?.LogDebug($"Buddy live mic chunk bytes={pcm.Length} inRms={inputRms:F5} outRms={outputRms:F4}.");
            }
        }

        private static void EndRecordAndCommit()
        {
            if (!_recording) return;
            try
            {
                FlushStreamingAudio(true);
                float duration = Time.unscaledTime - _startedAt;
                try { Microphone.End(_micDevice); } catch { }
                VoiceCoexistence.EndBuddyCapture();

                float cumulativeRms = _inputFrames > 0
                    ? (float)Math.Sqrt(_inputSquares / _inputFrames)
                    : 0f;
                if (_clip == null || _inputFrames < RequestedSampleRate / 5 || duration < 0.35f)
                {
                    Plugin.Log?.LogInfo("Voice stream too short; discarded.");
                    OpenAiRealtimeVoiceClient.AbortStreamingVoice(_streamId);
                    return;
                }
                if (!VoiceSignalMath.HasUsableSignal(cumulativeRms))
                {
                    Plugin.Log?.LogInfo($"Voice stream contained no usable signal (rms={cumulativeRms:F5}).");
                    OpenAiRealtimeVoiceClient.AbortStreamingVoice(_streamId);
                    MaybeHint("Buddy heard silence. Set Voice.InputDevice if Windows chose the wrong mic.");
                    return;
                }

                if (!OpenAiRealtimeVoiceClient.EndStreamingVoice(_streamId))
                    MaybeHint("Buddy couldn't finish the Realtime turn. Try again.");
                else
                    Plugin.Log?.LogInfo($"Committed live Realtime voice turn duration={duration:F2}s inputRms={cumulativeRms:F5}.");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError("EndRecordAndCommit: " + ex);
                OpenAiRealtimeVoiceClient.AbortStreamingVoice(_streamId);
            }
            finally
            {
                CleanupCapture();
            }
        }

        private static void AbortRecord(string hint)
        {
            if (_streamId != 0) OpenAiRealtimeVoiceClient.AbortStreamingVoice(_streamId);
            try { Microphone.End(_micDevice); } catch { }
            try { VoiceCoexistence.EndBuddyCapture(); } catch { }
            CleanupCapture();
            if (!string.IsNullOrEmpty(hint)) MaybeHint(hint);
        }

        private static void CleanupCapture()
        {
            _recording = false;
            _streamId = 0;
            _lastSampleFrame = 0;
            _streamGain = 0f;
            _inputSquares = 0d;
            _inputFrames = 0;
            if (_clip != null)
            {
                AudioClip old = _clip;
                _clip = null;
                try { UnityEngine.Object.Destroy(old); } catch { }
            }
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
