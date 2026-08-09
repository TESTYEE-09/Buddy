using System;
using System.Reflection;
using Dissonance;
using UnityEngine;

namespace LethalAICrewmate
{
    /// <summary>
    /// Keeps normal Lethal Company voice chat working while somebody is talking to Buddy.
    ///
    /// Unity's Microphone API is global per device. Buddy's push-to-talk calls Microphone.Start
    /// and Microphone.End on whatever device Dissonance is already capturing, which takes the
    /// device over for the recording and then leaves Dissonance's capture stopped afterwards —
    /// so teammates stop hearing that player. This restores Dissonance's capture as soon as Buddy
    /// is done with the device.
    ///
    /// It deliberately never changes the player's own mute state: a self-mute is their choice, and
    /// silently broadcasting someone who muted themselves would be worse than the bug it fixes.
    /// </summary>
    internal static class VoiceCoexistence
    {
        private static bool _sharedDeviceInUse;
        private static bool _resetUnavailable;
        private static float _lastRestoreAt = -999f;

        private static bool Enabled => Plugin.KeepGameVoiceDuringPtt?.Value != false;

        /// <summary>Called immediately before Buddy takes the microphone.</summary>
        internal static void BeginBuddyCapture(string buddyDevice)
        {
            if (!Enabled) return;
            try
            {
                DissonanceComms comms = FindComms();
                if (comms == null) return;

                // A deliberate self-mute is the player's own choice and is never touched here:
                // this only records whether Buddy is about to take the device the game is using.
                _sharedDeviceInUse = IsSameDevice(buddyDevice, ActiveDissonanceDevice(comms));
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogDebug("Voice coexistence begin: " + ex.Message);
            }
        }

        /// <summary>Called immediately after Buddy releases the microphone.</summary>
        internal static void EndBuddyCapture()
        {
            if (!Enabled) return;
            try
            {
                if (!_sharedDeviceInUse) return;
                _sharedDeviceInUse = false;

                // Rate-limit: a rapid PTT tap must not restart Dissonance capture repeatedly.
                if (Time.unscaledTime - _lastRestoreAt < 0.5f) return;
                _lastRestoreAt = Time.unscaledTime;

                DissonanceComms comms = FindComms();
                if (comms == null) return;
                RestartDissonanceCapture(comms);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogDebug("Voice coexistence end: " + ex.Message);
            }
        }

        private static DissonanceComms FindComms()
        {
            try { return UnityEngine.Object.FindObjectOfType<DissonanceComms>(); }
            catch { return null; }
        }

        private static string ActiveDissonanceDevice(DissonanceComms comms)
        {
            try
            {
                string device = comms?.MicrophoneCapture?.Device;
                if (string.IsNullOrWhiteSpace(device)) device = comms?.MicrophoneName;
                return device;
            }
            catch { return null; }
        }

        /// <summary>
        /// An empty Buddy device name means "Windows default", which is also what Dissonance uses
        /// when it has no explicit selection, so both empty names count as the same device.
        /// </summary>
        private static bool IsSameDevice(string buddyDevice, string gameDevice)
        {
            bool buddyDefault = string.IsNullOrWhiteSpace(buddyDevice);
            bool gameDefault = string.IsNullOrWhiteSpace(gameDevice);
            if (buddyDefault || gameDefault) return true;
            return string.Equals(buddyDevice.Trim(), gameDevice.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Dissonance's reset entry point has changed name across versions, so it is resolved
        /// reflectively and the failure is remembered rather than retried every push-to-talk.
        /// </summary>
        private static void RestartDissonanceCapture(DissonanceComms comms)
        {
            if (_resetUnavailable) return;
            string[] candidates = { "ResetMicrophoneCapture", "RestartMicrophoneCapture", "ResetMicrophone" };
            foreach (string name in candidates)
            {
                try
                {
                    MethodInfo method = typeof(DissonanceComms).GetMethod(
                        name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        null, Type.EmptyTypes, null);
                    if (method == null) continue;
                    method.Invoke(comms, null);
                    return;
                }
                catch (Exception ex)
                {
                    Plugin.Log?.LogDebug("Dissonance capture restart via " + name + ": " + ex.Message);
                }
            }

            _resetUnavailable = true;
            Plugin.Log?.LogWarning(
                "Could not restart Lethal Company voice capture after Buddy push-to-talk. " +
                "If teammates stop hearing you, set Voice.InputDevice to a different microphone than the game uses.");
        }

        internal static void ResetSession()
        {
            _sharedDeviceInUse = false;
            _resetUnavailable = false;
            _lastRestoreAt = -999f;
        }
    }
}
