using System;
using HarmonyLib;

namespace LethalAICrewmate
{
    /// <summary>
    /// Movement is already parsed deterministically from the player's chat before the LLM request.
    /// Strip model-produced movement tags without executing them so prompt injection/hallucination
    /// cannot mutate Buddy state.
    /// </summary>
    [HarmonyPatch(typeof(LlmClient), "ExtractMoveTag")]
    internal static class Patch_LlmClient_BlockModelMovement
    {
        [HarmonyPrefix]
        private static bool Prefix(ref string display, ref string tag)
        {
            try
            {
                tag = null;
                string value = display ?? "";
                string[] tags = { "[FOLLOW]", "[STAY]", "[SHIP]", "[FETCH]" };
                foreach (string controlTag in tags)
                    value = value.Replace(controlTag, "");
                display = value.Trim();
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"LLM movement-tag safety: {ex.Message}");
                tag = null;
            }
            return false;
        }
    }

    [HarmonyPatch(typeof(LlmClient), "BuildSystemPrompt")]
    internal static class Patch_LlmClient_NoControlTagsInPrompt
    {
        [HarmonyPostfix]
        private static void Postfix(ref string __result)
        {
            try
            {
                if (string.IsNullOrEmpty(__result)) return;
                const string oldRule = "Movement tags: [FOLLOW] [STAY] [SHIP] [FETCH]. ";
                const string newRule = "Never emit control tags; movement is handled deterministically from explicit player commands before you reply. ";
                __result = __result.Replace(oldRule, newRule);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"No-control-tag prompt patch: {ex.Message}");
            }
        }
    }
}
