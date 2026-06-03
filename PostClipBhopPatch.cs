using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace RedAllianceSpeedrun
{
    // Optional friction suppression during FixedUpdate catch-up after a clip-snap.
    //
    // Unity catch-up loop fires ~15 FixedUpdates per 300ms lag. If the player is grounded
    // after clip, MoveGround friction multiplies velocity by ~0.85 per tick (15% loss),
    // gutting speed in <10 ticks.
    //
    // Hold curBhopBuffer at 0 in CasualController's Prefix so MoveGround takes the bhop
    // branch (curAirAcceleration, no friction) for N ticks. Velocity cap from Accelerate
    // still applies on the input-direction axis — pure inertia survives, forward-pressed
    // axis still caps at maxVelocityGround unless PostClipPreserveForward is on too.
    //
    // Default off (PostClipPreserveSpeed=false) — native physics works for most clips.
    [HarmonyPatch(typeof(FPSWalker), "CasualController")]
    internal static class PostClipBhopPatch
    {
        private static FieldInfo _bhopField;
        private static int _ticksRemaining;
        private static bool _failed;

        internal static bool IsArmed => _ticksRemaining > 0;

        public static void Trigger()
        {
            if (!Plugin.PostClipPreserveSpeed) return;
            _ticksRemaining = Plugin.PostClipBhopTicks;
            if (Plugin.LogClipEvents)
                Plugin.Logger.LogInfo($"[postclip] bhop-window armed for {_ticksRemaining} FixedUpdate ticks");
        }

        [HarmonyPrefix]
        private static void Prefix(FPSWalker __instance)
        {
            if (_ticksRemaining <= 0) return;
            if (_failed) return;

            if ((object)_bhopField == null)
            {
                _bhopField = typeof(FPSWalker).GetField("curBhopBuffer",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if ((object)_bhopField == null)
                {
                    _failed = true;
                    Plugin.Logger.LogError("[postclip] could not resolve curBhopBuffer field; patch disabled.");
                    return;
                }
            }

            try
            {
                // CasualController will += fixedDeltaTime immediately after this Prefix returns.
                // Setting to 0 means buffer is 0.02 when MoveGround compares to bhopBuffer=0.042.
                _bhopField.SetValue(__instance, 0f);
                _ticksRemaining--;
            }
            catch (Exception e)
            {
                _failed = true;
                Plugin.Logger.LogError("[postclip] PostClipBhopPatch failed: " + e);
            }
        }
    }
}
