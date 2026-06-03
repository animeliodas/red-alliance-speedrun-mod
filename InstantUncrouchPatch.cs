using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace RedAllianceSpeedrun
{
    // Same-frame instant snap on uncrouch during a lag spike.
    //
    // OLD: SetCrouchingState(false) → coroutine UnCrouch() ran first iteration same-frame
    // with t = 25 * Time.deltaTime. dt >= 0.04s clamped Lerp to 1 → capsule snapped instantly
    // → CC penetration resolution sometimes pushed through thin walls.
    //
    // NEW: dev moved the lerp to CrouchingCapsule() in Update BEFORE input handling, so the
    // snap is delayed to next frame with normal dt → smooth lerp → no clip. AllowClip flag
    // only swaps Time.deltaTime vs fixedDeltaTime in the lerp formula, not the call order.
    //
    // This Postfix forces the OLD same-frame snap. Paired with ClipPenetrationPushPatch
    // which then emulates Unity 2017 CC penetration resolution.
    [HarmonyPatch(typeof(FPSWalker), "SetCrouchingState")]
    internal static class InstantUncrouchPatch
    {
        private static FieldInfo _charControllerField;
        private static FieldInfo _capsuleColliderField;
        private static FieldInfo _defaultHeightField;
        private static FieldInfo _crouchStateField;
        private static bool _failed;

        [HarmonyPostfix]
        private static void Postfix(FPSWalker __instance, bool state)
        {
            if (!Plugin.InstantUncrouch) return;
            if (state) return; // only on uncrouch
            if (_failed) return;

            // Only snap during a lag frame. The original bug only fired when Time.deltaTime
            // was high enough that the Lerp factor clamped to 1.0. Mimic that gate here so
            // normal uncrouch on smooth frames stays smooth (no accidental floor clips).
            float dtMs = Time.unscaledDeltaTime * 1000f;
            if (dtMs < Plugin.InstantUncrouchMinDeltaMs) return;

            try
            {
                if (!EnsureFields()) return;

                var charController = _charControllerField.GetValue(__instance) as CharacterController;
                var capsuleCollider = _capsuleColliderField.GetValue(__instance) as CapsuleCollider;
                float defaultHeight = (float)_defaultHeightField.GetValue(__instance);

                if ((object)charController != null)
                {
                    charController.height = defaultHeight;
                    charController.center = Vector3.zero;
                }
                if ((object)capsuleCollider != null)
                {
                    capsuleCollider.height = defaultHeight;
                    capsuleCollider.center = Vector3.zero;
                }
                // Mark transition complete so CrouchingCapsule doesn't undo it next frame.
                // The enum is FPSWalker+CrouchState; value 0 = None.
                if ((object)_crouchStateField != null)
                {
                    _crouchStateField.SetValue(__instance, Enum.ToObject(_crouchStateField.FieldType, 0));
                }

                // Arm bhop window — disables friction during FixedUpdate catch-up if
                // PostClipPreserveSpeed is on. No-op when off.
                PostClipBhopPatch.Trigger();
                // Emulate 2017 CC penetration response — iterative push along velocity.
                ClipPenetrationPushPatch.Push(__instance);
            }
            catch (Exception e)
            {
                _failed = true;
                Plugin.Logger.LogError("[clip] InstantUncrouchPatch failed: " + e);
            }
        }

        private static bool EnsureFields()
        {
            const BindingFlags B = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            if ((object)_charControllerField == null)
                _charControllerField = typeof(FPSWalker).GetField("charController", B);
            if ((object)_capsuleColliderField == null)
                _capsuleColliderField = typeof(FPSWalker).GetField("capsuleCollider", B);
            if ((object)_defaultHeightField == null)
                _defaultHeightField = typeof(FPSWalker).GetField("defaultControllerHeight", B);
            if ((object)_crouchStateField == null)
                _crouchStateField = typeof(FPSWalker).GetField("crouchState", B);

            if ((object)_charControllerField == null ||
                (object)_capsuleColliderField == null ||
                (object)_defaultHeightField == null)
            {
                _failed = true;
                Plugin.Logger.LogError("[clip] InstantUncrouchPatch: could not resolve one or more fields on FPSWalker.");
                return false;
            }
            return true;
        }
    }
}
