using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace RedAllianceSpeedrun
{
    // Quake-style positive-num2 clamp for Accelerate during the bhop window.
    //
    // Stock Accelerate, when projected speed exceeds max_velocity, returns NEGATIVE num2
    // and yanks forward-axis velocity back down to the cap in a single tick. Classical
    // Quake/Source PM_Accelerate refuses to decelerate via the cap path — it just stops
    // adding. We clamp num2 >= 0 only while PostClipBhopPatch is armed so normal gameplay
    // is unaffected. Sliding/slope projection preserved.
    //
    // Requires PostClipPreserveSpeed=true (gates IsArmed). Default off.
    [HarmonyPatch(typeof(FPSWalker), "Accelerate")]
    internal static class PostClipAcceleratePatch
    {
        private static FieldInfo _slidingField;
        private static FieldInfo _slopeMovementField;
        private static bool _failed;

        [HarmonyPrefix]
        private static bool Prefix(
            FPSWalker __instance,
            Vector3 accelDir,
            Vector3 prevVelocity,
            float accelerate,
            float max_velocity,
            ref Vector3 __result)
        {
            if (!Plugin.PostClipPreserveSpeed) return true;
            if (!Plugin.PostClipPreserveForward) return true;
            if (!PostClipBhopPatch.IsArmed) return true;
            if (_failed) return true;

            try
            {
                if ((object)_slidingField == null || (object)_slopeMovementField == null)
                {
                    const BindingFlags B = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                    _slidingField = typeof(FPSWalker).GetField("sliding", B);
                    _slopeMovementField = typeof(FPSWalker).GetField("slopeMovement", B);
                    if ((object)_slidingField == null || (object)_slopeMovementField == null)
                    {
                        _failed = true;
                        Plugin.Logger.LogError("[postclip] could not resolve sliding/slopeMovement fields; forward-preserve disabled.");
                        return true;
                    }
                }

                float num = Vector3.Dot(prevVelocity, accelDir);
                float num2 = accelerate * Time.fixedDeltaTime * accelDir.magnitude;
                if (num + num2 > max_velocity)
                {
                    num2 = max_velocity - num;
                }
                // The fix: never apply NEGATIVE acceleration via the speed cap. If we're
                // already over the limit, just don't add more — don't drag the player back.
                if (num2 < 0f) num2 = 0f;

                Vector3 vector = prevVelocity + accelDir * num2;

                bool sliding = (bool)_slidingField.GetValue(__instance);
                if (sliding)
                {
                    Vector3 slopeMovement = (Vector3)_slopeMovementField.GetValue(__instance);
                    vector = Vector3.ProjectOnPlane(vector, slopeMovement);
                }

                __result = vector;
                return false; // skip original Accelerate
            }
            catch (Exception e)
            {
                _failed = true;
                Plugin.Logger.LogError("[postclip] PostClipAcceleratePatch failed: " + e);
                return true; // fall through to original on error
            }
        }
    }
}
