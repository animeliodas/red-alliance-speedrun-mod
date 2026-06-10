using System;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;

namespace RedAllianceSpeedrun
{
    // Wrap the periodic InvokeRepeating callbacks that fire roughly every 1 second across the
    // scene. The user's steady-state stutter has ~1100ms cadence which matches these.
    // We time every call and aggregate; the Plugin periodically dumps the slowest 10.
    internal static class InvokeRepeatingProfiler
    {
        private struct Stats
        {
            public long TotalTicks;
            public long MaxTicks;
            public int CallCount;
        }

        private static readonly Dictionary<string, Stats> _stats = new Dictionary<string, Stats>(64);

        // No lock: Harmony postfix callbacks and the F11 handler both run on Unity's main
        // thread (Mono `lock` compiles to Monitor.Enter(obj, ref bool) which doesn't exist
        // in Unity 2017.4's Mono — and we don't need synchronization anyway).
        internal static void Record(string key, long elapsedTicks)
        {
            Stats s;
            _stats.TryGetValue(key, out s);
            s.TotalTicks += elapsedTicks;
            if (elapsedTicks > s.MaxTicks) s.MaxTicks = elapsedTicks;
            s.CallCount++;
            _stats[key] = s;
        }

        internal static void DumpAndReset()
        {
            Plugin.Logger.LogMessage("[profiler] DumpAndReset() called");
            List<KeyValuePair<string, Stats>> snapshot;
            try
            {
                snapshot = new List<KeyValuePair<string, Stats>>(_stats);
                _stats.Clear();
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError("[profiler] snapshot failed: " + e);
                return;
            }

            if (snapshot.Count == 0)
            {
                Plugin.Logger.LogMessage("[profiler] no InvokeRepeating callbacks recorded.");
                return;
            }

            snapshot.Sort((a, b) => b.Value.TotalTicks.CompareTo(a.Value.TotalTicks));
            int top = Math.Min(15, snapshot.Count);
            var sb = new System.Text.StringBuilder();
            sb.Append("[profiler] hot InvokeRepeating callbacks (total_ms / calls / max_ms / avg_ms):\n");
            double ticksPerMs = Stopwatch.Frequency / 1000.0;
            for (int i = 0; i < top; i++)
            {
                var k = snapshot[i].Key;
                var s = snapshot[i].Value;
                double totalMs = s.TotalTicks / ticksPerMs;
                double maxMs = s.MaxTicks / ticksPerMs;
                double avgMs = s.CallCount > 0 ? totalMs / s.CallCount : 0;
                sb.Append($"  {k,-60} {totalMs,8:F1} / {s.CallCount,6} / {maxMs,7:F1} / {avgMs,6:F2}\n");
            }
            Plugin.Logger.LogMessage(sb.ToString());
        }
    }

    [HarmonyPatch(typeof(GlobalAIScript), "GetDistanceToPlayerCamera")]
    internal static class Patch_GetDistanceToPlayerCamera
    {
        [HarmonyPrefix] static void Pre(out Stopwatch __state) { __state = Stopwatch.StartNew(); }
        [HarmonyPostfix] static void Post(Stopwatch __state) { __state.Stop(); InvokeRepeatingProfiler.Record("GlobalAIScript.GetDistanceToPlayerCamera", __state.ElapsedTicks); }
    }

    [HarmonyPatch(typeof(GlobalAIScript), "ForceUpdateTarget")]
    internal static class Patch_ForceUpdateTarget
    {
        [HarmonyPrefix] static void Pre(out Stopwatch __state) { __state = Stopwatch.StartNew(); }
        [HarmonyPostfix] static void Post(Stopwatch __state) { __state.Stop(); InvokeRepeatingProfiler.Record("GlobalAIScript.ForceUpdateTarget", __state.ElapsedTicks); }
    }

    [HarmonyPatch(typeof(GlobalAIScript), "UpdateMeshShadows")]
    internal static class Patch_UpdateMeshShadows
    {
        [HarmonyPrefix] static void Pre(out Stopwatch __state) { __state = Stopwatch.StartNew(); }
        [HarmonyPostfix] static void Post(Stopwatch __state) { __state.Stop(); InvokeRepeatingProfiler.Record("GlobalAIScript.UpdateMeshShadows", __state.ElapsedTicks); }
    }

    [HarmonyPatch(typeof(GlobalAIScript), "UpdateTargetCondition")]
    internal static class Patch_UpdateTargetCondition
    {
        [HarmonyPrefix] static void Pre(out Stopwatch __state) { __state = Stopwatch.StartNew(); }
        [HarmonyPostfix] static void Post(Stopwatch __state) { __state.Stop(); InvokeRepeatingProfiler.Record("GlobalAIScript.UpdateTargetCondition", __state.ElapsedTicks); }
    }

    [HarmonyPatch(typeof(GlobalAIScript), "GetClosestTarget")]
    internal static class Patch_GetClosestTarget
    {
        [HarmonyPrefix] static void Pre(out Stopwatch __state) { __state = Stopwatch.StartNew(); }
        [HarmonyPostfix] static void Post(Stopwatch __state) { __state.Stop(); InvokeRepeatingProfiler.Record("GlobalAIScript.GetClosestTarget", __state.ElapsedTicks); }
    }

    [HarmonyPatch(typeof(LightDistanceCullingScript), "CheckDistance")]
    internal static class Patch_CheckDistance
    {
        [HarmonyPrefix] static void Pre(out Stopwatch __state) { __state = Stopwatch.StartNew(); }
        [HarmonyPostfix] static void Post(Stopwatch __state) { __state.Stop(); InvokeRepeatingProfiler.Record("LightDistanceCullingScript.CheckDistance", __state.ElapsedTicks); }
    }

    [HarmonyPatch(typeof(ObjectDisableScript), "DisableCheck")]
    internal static class Patch_DisableCheck
    {
        [HarmonyPrefix] static void Pre(out Stopwatch __state) { __state = Stopwatch.StartNew(); }
        [HarmonyPostfix] static void Post(Stopwatch __state) { __state.Stop(); InvokeRepeatingProfiler.Record("ObjectDisableScript.DisableCheck", __state.ElapsedTicks); }
    }

    [HarmonyPatch(typeof(FootStepsScriptNew), "DistanceCheck")]
    internal static class Patch_DistanceCheck
    {
        [HarmonyPrefix] static void Pre(out Stopwatch __state) { __state = Stopwatch.StartNew(); }
        [HarmonyPostfix] static void Post(Stopwatch __state) { __state.Stop(); InvokeRepeatingProfiler.Record("FootStepsScriptNew.DistanceCheck", __state.ElapsedTicks); }
    }

    [HarmonyPatch(typeof(AILightHeightOptimizationScript), "CheckHeight")]
    internal static class Patch_CheckHeight
    {
        [HarmonyPrefix] static void Pre(out Stopwatch __state) { __state = Stopwatch.StartNew(); }
        [HarmonyPostfix] static void Post(Stopwatch __state) { __state.Stop(); InvokeRepeatingProfiler.Record("AILightHeightOptimizationScript.CheckHeight", __state.ElapsedTicks); }
    }
}
