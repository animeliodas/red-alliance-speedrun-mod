using HarmonyLib;
using UnityEngine;

namespace RedAllianceSpeedrun
{
    // OnEnemyKilledAllertEnemiesScript.Update walks its whole NPC array every frame
    // (Vector3.Distance + several property reads per NPC) until the alert fires - measured
    // 258us per frame during combat. The check is a polling watchdog: whether it notices the
    // trigger condition this frame or two frames later is imperceptible (at sprint speed the
    // player moves ~25cm in 30ms), and the alert chain itself fires identically.
    //
    // Run the body only every Nth frame, staggered per instance so multiple alert scripts in
    // a scene don't all check on the same frame.
    [HarmonyPatch(typeof(OnEnemyKilledAllertEnemiesScript), "Update")]
    internal static class AlertCheckThrottlePatch
    {
        // Set from config at startup. 1 = no throttling.
        internal static int Interval = 2;

        [HarmonyPrefix]
        public static bool Prefix(OnEnemyKilledAllertEnemiesScript __instance)
        {
            if (Interval <= 1) return true;
            int stagger = __instance.GetInstanceID() & 0x7FFFFFFF;
            return (Time.frameCount + stagger) % Interval == 0;
        }
    }
}
