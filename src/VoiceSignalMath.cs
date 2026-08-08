using System;

namespace LethalAICrewmate
{
    internal static class VoiceSignalMath
    {
        internal const float MinInputRms = 0.00055f;

        internal static bool HasUsableSignal(float inputRms) =>
            !float.IsNaN(inputRms) && !float.IsInfinity(inputRms) && inputRms >= MinInputRms;

        internal static float CalculateGain(float inputRms, float peak, float targetRms = 0.10f, float maxGain = 30f)
        {
            if (inputRms <= 0.000001f || float.IsNaN(inputRms) || float.IsInfinity(inputRms))
                return 1f;
            float rmsGain = Math.Max(1f, Math.Min(maxGain, targetRms / inputRms));
            float peakGain = peak > 0.000001f ? 0.92f / peak : maxGain;
            return Math.Max(1f, Math.Min(maxGain, Math.Min(rmsGain, peakGain)));
        }
    }
}
