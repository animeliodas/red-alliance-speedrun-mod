using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RedAlliance.Input;
using UnityEngine;

namespace RedAllianceSpeedrun
{
    // Replaces InputManager.Update with an allocation-free version. The original allocates
    // every frame:
    //   1) UnityEngine.Input.GetJoystickNames() returns a fresh string[] per call.
    //   2) Dictionary.ElementAt(i) is a LINQ method that allocates an enumerator wrapper
    //      AND iterates from the start each call (O(n²) total for the whole loop).
    // For ~29 ButtonKeys at 60 FPS that's ~1800 GC-allocations/sec on a method called
    // every frame.
    [HarmonyPatch(typeof(InputManager), "Update")]
    internal static class InputManagerUpdatePatch
    {
        // Cache joystick names. Unity only adds/removes joysticks via OnJoystickConnected
        // (rare), so polling once per second is more than enough.
        private static string[] _cachedJoyNames = new string[0];
        private static float _lastJoyPollTime;

        // Backing field for ControllerConnected (auto-property with private setter)
        private static FieldInfo _controllerConnectedField;

        private static void SetControllerConnected(bool value)
        {
            if (_controllerConnectedField == null)
            {
                _controllerConnectedField = typeof(InputManager).GetField(
                    "<ControllerConnected>k__BackingField",
                    BindingFlags.Static | BindingFlags.NonPublic);
            }
            if ((object)_controllerConnectedField != null)
            {
                _controllerConnectedField.SetValue(null, value);
            }
        }

        [HarmonyPrefix]
        private static bool ReplaceUpdate()
        {
            // Poll joystick names at most once per second
            float now = Time.unscaledTime;
            if (now - _lastJoyPollTime >= 1f)
            {
                _cachedJoyNames = UnityEngine.Input.GetJoystickNames();
                _lastJoyPollTime = now;
            }

            SetControllerConnected(InputManager.ControllerEnabled && _cachedJoyNames.Length > 0);

            var buttons = InputManager.ButtonKeys;
            if (buttons == null) return false;

            float dt = Time.deltaTime;

            // Allocation-free foreach over Dictionary.Values (struct enumerator)
            foreach (var btn in buttons.Values)
            {
                if (btn == null) continue;
                if (btn.inputType != InputType.Axis) continue;

                if (btn.readFromUnityInput)
                {
                    btn.currentAxis = UnityEngine.Input.GetAxis(btn.axisToRead);
                    continue;
                }

                btn.negative = UnityEngine.Input.GetKey(btn.negativeKeyCode);
                btn.positive = UnityEngine.Input.GetKey(btn.positiveKeyCode);
                float target = btn.negative ? -1f : (btn.positive ? 1f : 0f);

                bool useSensitivity =
                    ((!btn.negative || !(btn.currentAxis > 0f)) &&
                     (!btn.positive || !(btn.currentAxis < 0f)) &&
                     (btn.negative || btn.positive));
                float maxDelta = useSensitivity
                    ? (dt * btn.sensitivity)
                    : (dt * btn.gravity);

                btn.currentAxis = Mathf.MoveTowards(btn.currentAxis, target, maxDelta);
            }

            return false; // skip original
        }
    }
}
