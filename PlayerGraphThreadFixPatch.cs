using System;
using System.Reflection;
using System.Threading;
using HarmonyLib;

namespace RedAllianceSpeedrun
{
    // PlayerGraphScript.Start() spawns `new Thread(Count).Start()`, where Count() is:
    //
    //     while (true)
    //     {
    //         memoryBytes = (int)GC.GetTotalMemory(forceFullCollection: true);
    //         cpuUsage = ReadCPUPC();
    //         Thread.Sleep(~statsRefreshRate seconds);
    //     }
    //
    // Two bugs compound across level loads:
    //   1. forceFullCollection:true forces a FULL blocking garbage collection every ~second.
    //   2. The loop has no exit condition, and a managed thread outlives its destroyed
    //      GameObject. The stats canvas is recreated on every level load, so every restart
    //      adds one more immortal thread forcing one more full GC per second: gc/s grows by
    //      ~1 per restart (measured 10.9 after 10 restarts) until the game stutters
    //      permanently after 20-30 loads.
    //
    // Replacement: a generation-tagged loop. Each new Count thread bumps the generation;
    // superseded threads notice and exit, so exactly one worker survives. The memory stat is
    // read WITHOUT forcing a collection; CPU usage is still updated via the original
    // ReadCPUPC.
    [HarmonyPatch(typeof(PlayerGraphScript), "Count")]
    internal static class PlayerGraphThreadFixPatch
    {
        private static volatile int s_generation;

        private static readonly AccessTools.FieldRef<PlayerGraphScript, int> MemoryBytesRef =
            AccessTools.FieldRefAccess<PlayerGraphScript, int>("memoryBytes");
        private static readonly AccessTools.FieldRef<PlayerGraphScript, float> CpuUsageRef =
            AccessTools.FieldRefAccess<PlayerGraphScript, float>("cpuUsage");
        private static readonly MethodInfo ReadCpuMethod =
            AccessTools.Method(typeof(PlayerGraphScript), "ReadCPUPC");

        [HarmonyPrefix]
        public static bool Prefix(PlayerGraphScript __instance)
        {
            int myGen = ++s_generation;
            Plugin.Logger.LogInfo($"[graphfix] stats thread gen {myGen} started; superseded threads will exit.");
            try
            {
                float refreshRate = __instance.statsRefreshRate;
                if (refreshRate <= 0f) refreshRate = 1f;
                while (s_generation == myGen)
                {
                    MemoryBytesRef(__instance) = (int)GC.GetTotalMemory(false);
                    try
                    {
                        if ((object)ReadCpuMethod != null)
                            CpuUsageRef(__instance) = Convert.ToSingle(ReadCpuMethod.Invoke(__instance, null));
                    }
                    catch { }
                    Thread.Sleep((int)(refreshRate * 1000f));
                }
            }
            catch { }
            Plugin.Logger.LogDebug($"[graphfix] stats thread gen {myGen} exited.");
            return false; // never run the original Count loop
        }
    }
}
