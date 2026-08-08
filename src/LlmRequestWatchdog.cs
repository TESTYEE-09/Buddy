using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace LethalAICrewmate
{
    /// <summary>
    /// Last-resort recovery for coroutine failures outside LlmClient's normal HTTP handling. A
    /// request should never permanently hold _inFlight and disable Buddy chat for the whole session.
    /// </summary>
    [HarmonyPatch(typeof(LlmClient), nameof(LlmClient.Tick))]
    internal static class Patch_LlmClient_RequestWatchdog
    {
        private const float HardRequestCeilingSeconds = 45f;

        private static readonly FieldInfo InFlightField = AccessTools.Field(typeof(LlmClient), "_inFlight");
        private static readonly FieldInfo LastCallTimeField = AccessTools.Field(typeof(LlmClient), "_lastCallTime");
        private static readonly FieldInfo RunningField = AccessTools.Field(typeof(LlmClient), "_running");

        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        private static void Prefix()
        {
            try
            {
                if (InFlightField == null || LastCallTimeField == null || RunningField == null)
                    return;

                bool inFlight = (bool)InFlightField.GetValue(null);
                if (!inFlight) return;

                float startedAt = (float)LastCallTimeField.GetValue(null);
                if (Time.time - startedAt <= HardRequestCeilingSeconds)
                    return;

                var running = RunningField.GetValue(null) as Coroutine;
                if (running != null && Plugin.Host != null)
                {
                    try { Plugin.Host.StopCoroutine(running); } catch { /* ignore */ }
                }

                RunningField.SetValue(null, null);
                InFlightField.SetValue(null, false);
                Plugin.Log?.LogWarning("Recovered an LLM request that exceeded the hard request ceiling.");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"LLM request watchdog: {ex.Message}");
            }
        }
    }
}
