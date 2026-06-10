using HarmonyLib;
using RedAlliance.Tools;
using UnityEngine;

namespace RedAllianceSpeedrun
{
    // LineRendererRopeScript recomputes the full catenary curve (per-subdivision trig and
    // square roots, plus LineRenderer.SetPosition calls and per-frame positionCount/material
    // reassignment) every frame while wind is enabled - including for ropes nobody can see.
    // It has a camera-distance cutoff but no frustum check, and it was the single biggest
    // Update-time consumer in both profiling sessions (top of every [uprof] table).
    //
    // Skip GenerateRope while the LineRenderer is not visible to any camera. The wind phase
    // is a continuous function of Time.time, so a rope re-entering the view continues its
    // animation with no popping. Purely visual script - gameplay untouched.
    [HarmonyPatch(typeof(LineRendererRopeScript), "GenerateRope")]
    internal static class RopeVisibilityPatch
    {
        private static readonly AccessTools.FieldRef<LineRendererRopeScript, LineRenderer> LineRendererRef =
            AccessTools.FieldRefAccess<LineRendererRopeScript, LineRenderer>("lineRenderer");

        [HarmonyPrefix]
        public static bool Prefix(LineRendererRopeScript __instance)
        {
            var lr = LineRendererRef(__instance);
            // First frame the field may still be unassigned (Update lazily fills it);
            // let the original run in that case.
            if (lr == null) return true;
            return lr.isVisible;
        }
    }
}
