using System;
using UnityEngine;

namespace LethalAICrewmate
{
    /// <summary>Drives locomotion parameters that vanilla Masked.Update would normally own.</summary>
    internal static class BuddyAnimation
    {
        private static readonly string[] MovingBools = { "IsRunning", "IsWalking", "isMoving", "Moving" };
        private static readonly string[] SpeedFloats = { "Speed", "speed", "MoveSpeed" };

        internal static void Apply(MaskedPlayerEnemy enemy, bool moving)
        {
            Animator animator = enemy?.creatureAnimator;
            if (animator == null || !animator.enabled) return;
            try
            {
                AnimatorControllerParameter[] parameters = animator.parameters;
                foreach (AnimatorControllerParameter parameter in parameters)
                {
                    if (parameter.type == AnimatorControllerParameterType.Bool && Contains(MovingBools, parameter.name))
                        animator.SetBool(parameter.nameHash, moving);
                    else if (parameter.type == AnimatorControllerParameterType.Float && Contains(SpeedFloats, parameter.name))
                        animator.SetFloat(parameter.nameHash, moving ? 1f : 0f, 0.12f, Time.deltaTime);
                }
            }
            catch (Exception ex) { Plugin.Log?.LogDebug("Buddy animation: " + ex.Message); }
        }

        private static bool Contains(string[] values, string candidate)
        {
            foreach (string value in values)
                if (string.Equals(value, candidate, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }
}
