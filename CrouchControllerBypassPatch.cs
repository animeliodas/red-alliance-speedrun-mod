using System;
using System.Reflection;
using HarmonyLib;

namespace RedAllianceSpeedrun
{
    // FPSWalker.CrouchController populates `curHitCrouchColliders` via
    // Physics.OverlapCapsuleNonAlloc to detect ceiling/wall above the crouched player.
    // The Update() method then checks `curHitCrouchColliders == 0` in 5 separate places
    // before allowing uncrouch (release-crouch, CrouchToggle, Run, etc.). If any collider
    // is detected, uncrouch is blocked.
    //
    // With only AllowClip = true, the Lerp t still snaps to 1 on lag, but uncrouch never
    // STARTS because SetCrouchingState(false) is never called. The dev kept this gate
    // outside the AllowClip flag, which is why only 1-2 of the original clips still work
    // (the ones where geometry happens to leave curHitCrouchColliders=0).
    //
    // This postfix zeroes curHitCrouchColliders after each call, making every gate pass.
    // Combined with AllowClip=true (which makes the Lerp use Time.deltaTime and instantly
    // snap during a screenshot-lag frame), all original clip-skips become reproducible.
    [HarmonyPatch(typeof(FPSWalker), "CrouchController")]
    internal static class CrouchControllerBypassPatch
    {
        private static FieldInfo _field;
        private static bool _failed;

        [HarmonyPostfix]
        private static void Postfix(FPSWalker __instance)
        {
            if (!Plugin.BypassCrouchCeiling) return;
            if (_failed) return;
            if ((object)_field == null)
            {
                try
                {
                    _field = typeof(FPSWalker).GetField("curHitCrouchColliders",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                }
                catch { _failed = true; return; }
                if ((object)_field == null) { _failed = true; return; }
            }
            try { _field.SetValue(__instance, 0); }
            catch (Exception e)
            {
                _failed = true;
                Plugin.Logger.LogError("[clip] CrouchControllerBypassPatch failed: " + e);
            }
        }
    }
}
