using System;
using HarmonyLib;

namespace LethalAICrewmate
{
    /// <summary>
    /// Explicit player chat commands are already executed deterministically by ChatObserver before
    /// the LLM is called. Never execute route/buy/terminal side effects a second time from model tags.
    /// This also prevents a hallucinated or prompt-injected model tag from spending crew credits.
    /// </summary>
    [HarmonyPatch(typeof(TerminalBuddy), nameof(TerminalBuddy.ApplyLlmTags))]
    internal static class Patch_TerminalBuddy_BlockLlmSideEffects
    {
        [HarmonyPrefix]
        private static bool Prefix(string display, ref string cleanedDisplay, ref string __result)
        {
            cleanedDisplay = StripTerminalTags(display ?? "");
            __result = null;
            return false;
        }

        private static string StripTerminalTags(string value)
        {
            string result = value ?? "";
            result = StripTag(result, "ROUTE");
            result = StripTag(result, "BUY");
            result = StripTag(result, "TERMINAL");
            return result.Trim();
        }

        private static string StripTag(string value, string tag)
        {
            string open = "[" + tag + ":";
            while (true)
            {
                int start = value.IndexOf(open, StringComparison.OrdinalIgnoreCase);
                if (start < 0) return value;
                int end = value.IndexOf(']', start);
                if (end < 0)
                    return value.Substring(0, start).TrimEnd();
                value = value.Remove(start, end - start + 1);
            }
        }
    }
}
