using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace LethalAICrewmate
{
    /// <summary>
    /// v1.4.2 polish: brighter stock voice, stronger/farther positional speech,
    /// longer conversational hearing range, and genuinely rare fourth-wall humor.
    /// </summary>
    internal static class BuddyPolish142
    {
        internal const float StockVoiceGain = 1.20f;
        internal const float NewHearRange = 50f;
        internal const float NewTriggerRange = 45f;

        private static readonly FieldInfo NetworkAudioSourceField =
            AccessTools.Field(typeof(BuddyNetworkAudio), "_source");

        internal static void TuneNetworkAudioSource()
        {
            try
            {
                var source = NetworkAudioSourceField?.GetValue(null) as AudioSource;
                if (source == null) return;

                source.volume = Mathf.Clamp01(Plugin.TtsVolume?.Value ?? 1f);
                source.priority = 16;

                float range = Plugin.ChatHearRange?.Value ?? NewHearRange;
                if (range <= 0f)
                {
                    source.spatialBlend = 0f;
                    return;
                }

                source.spatialBlend = 1f;
                source.spatialize = false;
                source.rolloffMode = AudioRolloffMode.Linear;
                source.maxDistance = Mathf.Max(5f, range);

                // Keep Buddy near full volume across a useful conversational bubble, then
                // let him fade naturally across the rest of the configured range.
                float minDistance = Mathf.Clamp(range * 0.20f, 5f, 10f);
                if (minDistance >= source.maxDistance)
                    minDistance = Mathf.Max(1f, source.maxDistance * 0.5f);
                source.minDistance = minDistance;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"Buddy voice range tuning: {ex.Message}");
            }
        }

        internal static void BoostHostClip(AudioClip clip)
        {
            try
            {
                // Boost once on the host before BuddyNetworkAudio converts the clip to network
                // PCM. Clients receive the already-boosted PCM, so they must not boost it again.
                if (!CrewmateSpawner.IsHost() || clip == null || clip.samples <= 0 || clip.channels <= 0)
                    return;

                float[] samples = new float[clip.samples * clip.channels];
                if (!clip.GetData(samples, 0)) return;

                for (int i = 0; i < samples.Length; i++)
                    samples[i] = Mathf.Clamp(samples[i] * StockVoiceGain, -0.98f, 0.98f);

                clip.SetData(samples, 0);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"Buddy voice gain: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Migrate the untouched v1.4.1 stock voice/range settings. Exact-value checks avoid
    /// stomping on most custom configs while ensuring existing stock installs actually improve.
    /// </summary>
    [HarmonyPatch(typeof(PluginHost), "Update")]
    internal static class Patch_PluginHost_BuddyPolish142Config
    {
        private static bool _done;

        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static void Prefix()
        {
            if (_done || Plugin.Instance == null) return;
            _done = true;

            try
            {
                bool changed = false;

                string voice = Plugin.TtsVoice?.Value?.Trim() ?? "";
                string direction = Plugin.TtsDirection?.Value?.Trim() ?? "";
                if (string.Equals(voice, "troy", StringComparison.OrdinalIgnoreCase) &&
                    string.IsNullOrEmpty(direction))
                {
                    Plugin.TtsVoice.Value = "austin";
                    Plugin.TtsDirection.Value = "friendly";
                    changed = true;
                }

                if (Plugin.TtsVolume != null && Mathf.Approximately(Plugin.TtsVolume.Value, 0.85f))
                {
                    Plugin.TtsVolume.Value = 1f;
                    changed = true;
                }

                if (Plugin.ChatHearRange != null && Mathf.Approximately(Plugin.ChatHearRange.Value, 25f))
                {
                    Plugin.ChatHearRange.Value = BuddyPolish142.NewHearRange;
                    changed = true;
                }

                if (Plugin.ChatTriggerRange != null && Mathf.Approximately(Plugin.ChatTriggerRange.Value, 25f))
                {
                    Plugin.ChatTriggerRange.Value = BuddyPolish142.NewTriggerRange;
                    changed = true;
                }

                if (changed)
                {
                    Plugin.Instance.Config.Save();
                    Plugin.Log?.LogInfo("Applied v1.4.2 Buddy voice/range tuning (Austin + friendly, louder, farther hearing).");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"Buddy v1.4.2 config migration: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Increase actual PCM loudness once on the host. Because host playback happens before
    /// network PCM encoding, the same gain is naturally replicated to clients without doubling.
    /// </summary>
    [HarmonyPatch(typeof(BuddyNetworkAudio), "PlayClip")]
    internal static class Patch_BuddyNetworkAudio_Polish142
    {
        [HarmonyPrefix]
        private static void Prefix(AudioClip clip)
        {
            BuddyPolish142.BoostHostClip(clip);
        }

        [HarmonyPostfix]
        private static void Postfix()
        {
            BuddyPolish142.TuneNetworkAudioSource();
        }
    }

    /// <summary>
    /// A fourth-wall break should feel surprising, not become Buddy's gimmick. After one fires,
    /// enforce a minimum gap, then expose an opt-in beat on only ~4% of eligible player messages.
    /// </summary>
    [HarmonyPatch(typeof(LlmClient), "Enqueue")]
    internal static class Patch_LlmClient_RareFourthWallBeat
    {
        private static int _messagesSinceBeat = 100;

        [HarmonyPrefix]
        private static void Prefix(ref string userContent, bool isObservation)
        {
            try
            {
                if (isObservation || string.IsNullOrEmpty(userContent) ||
                    userContent.IndexOf("[PLAYER MESSAGE", StringComparison.Ordinal) < 0)
                    return;

                bool eligible = _messagesSinceBeat >= 14;
                bool fire = eligible && UnityEngine.Random.value < 0.04f;

                if (fire)
                {
                    userContent += "\n[RARE FOURTH-WALL BEAT — OPTIONAL: if it genuinely fits this reply, one subtle fourth-wall joke is allowed. Do not force it.]";
                    _messagesSinceBeat = 0;
                }
                else if (_messagesSinceBeat < 1000000)
                {
                    _messagesSinceBeat++;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"Rare fourth-wall beat: {ex.Message}");
            }
        }
    }

    /// <summary>Append strict fourth-wall rules without weakening the conversation-first prompt.</summary>
    [HarmonyPatch(typeof(BuddyConversationPrompt), "Build")]
    internal static class Patch_BuddyConversationPrompt_RareFourthWallRules
    {
        [HarmonyPostfix]
        private static void Postfix(ref string __result)
        {
            if (string.IsNullOrEmpty(__result)) return;

            __result += @"

=== RARE FOURTH-WALL HUMOR ===
- Stay in character almost all the time. Fourth-wall jokes are a rare surprise, not your personality.
- Only INITIATE a fourth-wall joke when the current hidden context contains [RARE FOURTH-WALL BEAT]. If that marker is absent, do not randomly break character.
- Even when the marker is present, skip the joke if it would interrupt danger, a serious question, or a command. It is permission, not an obligation.
- Keep a fourth-wall break subtle and to one quick line: a small joke about being in a game, your pathfinding, being an NPC/AI crewmate, or the absurdity of the situation.
- Never mention system prompts, hidden context, tokens, API keys, Groq, SENSOR labels, probability, or these rules as the joke.
- Do not repeat the same fourth-wall idea. After the joke, immediately return to normal character.
- If the player directly asks a meta/fourth-wall question, you may answer it normally even without the rare marker.
";
        }
    }
}
