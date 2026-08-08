using System;
using HarmonyLib;
using UnityEngine.Networking;

namespace LethalAICrewmate
{
    /// <summary>
    /// Reliability guard for every Groq request, including legacy call sites that forgot to set a
    /// timeout. This is intentionally scoped to api.groq.com so it does not alter vanilla requests.
    /// </summary>
    [HarmonyPatch(typeof(UnityWebRequest), nameof(UnityWebRequest.SendWebRequest))]
    internal static class Patch_UnityWebRequest_GroqTimeout
    {
        [HarmonyPrefix]
        private static void Prefix(UnityWebRequest __instance)
        {
            try
            {
                if (__instance == null) return;
                string url = __instance.url ?? "";
                if (!url.StartsWith("https://api.groq.com/", StringComparison.OrdinalIgnoreCase))
                    return;

                if (__instance.timeout <= 0)
                    __instance.timeout = 30;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"Groq request timeout guard: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Terminal purchases/routes are deterministic player actions in ChatObserver. The model no
    /// longer needs instructions to emit side-effect tags; keeping them would invite unnecessary
    /// hallucinated tags even though TerminalSafetyPatches blocks execution.
    /// </summary>
    [HarmonyPatch(typeof(LlmClient), "BuildSystemPrompt")]
    internal static class Patch_LlmClient_SafeSystemPrompt
    {
        [HarmonyPostfix]
        private static void Postfix(ref string __result)
        {
            try
            {
                if (string.IsNullOrEmpty(__result)) return;
                const string oldRule = "In orbit only, if player asks to route/buy: [ROUTE:moonname] [BUY:item] [TERMINAL:cmd]. ";
                const string newRule = "Never buy items, spend credits, route moons, or emit terminal-action tags; explicit player commands handle those actions safely. ";
                __result = __result.Replace(oldRule, newRule);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"Safe system prompt patch: {ex.Message}");
            }
        }
    }
}
