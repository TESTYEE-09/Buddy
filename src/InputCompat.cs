using System;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace LethalAICrewmate
{
    /// <summary>
    /// Lethal Company uses the new Input System only — UnityEngine.Input throws.
    /// </summary>
    public static class InputCompat
    {
        private static bool _loggedOnce;

        public static bool GetKey(KeyCode key)
        {
            try
            {
                var k = Keyboard.current;
                if (k == null) return false;
                var control = Map(key);
                return control != null && control.isPressed;
            }
            catch (Exception ex)
            {
                LogOnce(ex);
                return false;
            }
        }

        public static bool GetKeyDown(KeyCode key)
        {
            try
            {
                var k = Keyboard.current;
                if (k == null) return false;
                var control = Map(key);
                return control != null && control.wasPressedThisFrame;
            }
            catch (Exception ex)
            {
                LogOnce(ex);
                return false;
            }
        }

        public static bool GetKeyUp(KeyCode key)
        {
            try
            {
                var k = Keyboard.current;
                if (k == null) return false;
                var control = Map(key);
                return control != null && control.wasReleasedThisFrame;
            }
            catch (Exception ex)
            {
                LogOnce(ex);
                return false;
            }
        }

        private static KeyControl Map(KeyCode key)
        {
            var kb = Keyboard.current;
            if (kb == null) return null;

            // Common PTT / command keys
            switch (key)
            {
                case KeyCode.V: return kb.vKey;
                case KeyCode.B: return kb.bKey;
                case KeyCode.N: return kb.nKey;
                case KeyCode.C: return kb.cKey;
                case KeyCode.X: return kb.xKey;
                case KeyCode.Z: return kb.zKey;
                case KeyCode.G: return kb.gKey;
                case KeyCode.H: return kb.hKey;
                case KeyCode.T: return kb.tKey;
                case KeyCode.Y: return kb.yKey;
                case KeyCode.U: return kb.uKey;
                case KeyCode.I: return kb.iKey;
                case KeyCode.O: return kb.oKey;
                case KeyCode.P: return kb.pKey;
                case KeyCode.F: return kb.fKey;
                case KeyCode.R: return kb.rKey;
                case KeyCode.Q: return kb.qKey;
                case KeyCode.E: return kb.eKey;
                case KeyCode.LeftAlt: return kb.leftAltKey;
                case KeyCode.RightAlt: return kb.rightAltKey;
                case KeyCode.LeftControl: return kb.leftCtrlKey;
                case KeyCode.RightControl: return kb.rightCtrlKey;
                case KeyCode.LeftShift: return kb.leftShiftKey;
                case KeyCode.RightShift: return kb.rightShiftKey;
                case KeyCode.CapsLock: return kb.capsLockKey;
                case KeyCode.Tab: return kb.tabKey;
                case KeyCode.Space: return kb.spaceKey;
                case KeyCode.Return: return kb.enterKey;
                case KeyCode.KeypadEnter: return kb.numpadEnterKey;
                default:
                    // Best-effort: letter keys A–Z
                    if (key >= KeyCode.A && key <= KeyCode.Z)
                    {
                        int idx = (int)key - (int)KeyCode.A;
                        return kb[(Key)((int)Key.A + idx)];
                    }
                    if (key >= KeyCode.Alpha0 && key <= KeyCode.Alpha9)
                    {
                        int idx = (int)key - (int)KeyCode.Alpha0;
                        return kb[(Key)((int)Key.Digit0 + idx)];
                    }
                    return null;
            }
        }

        private static void LogOnce(Exception ex)
        {
            if (_loggedOnce) return;
            _loggedOnce = true;
            Plugin.Log?.LogWarning($"InputCompat: {ex.Message}");
        }
    }
}
