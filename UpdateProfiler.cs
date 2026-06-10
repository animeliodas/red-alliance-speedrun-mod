using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace RedAllianceSpeedrun
{
    // Patches Update/LateUpdate/FixedUpdate of every game MonoBehaviour subclass that
    // declares one. Times every call with Stopwatch, aggregates by type+method.
    // F11 dumps top consumers.
    internal static class UpdateProfiler
    {
        private struct Stats
        {
            public long TotalTicks;
            public long MaxTicks;
            public int CallCount;
        }

        // Indexed by "TypeName.MethodName". Main-thread only — no lock (Mono 2.0 lacks
        // Monitor.Enter(obj, ref bool) anyway).
        private static readonly Dictionary<string, Stats> _stats = new Dictionary<string, Stats>(256);

        // Pre-built lookup so Pre/Post don't allocate strings on the hot path.
        // Key is the MethodBase reference (RuntimeMethodHandle would be smaller but the
        // patch passes MethodBase directly).
        internal static readonly Dictionary<MethodBase, string> KeyMap = new Dictionary<MethodBase, string>(256);

        // Stopwatch state for the active patched call. Stack-like to handle reentry edge
        // cases. We only ever index by depth in the call.
        private static readonly Stack<Stopwatch> _swPool = new Stack<Stopwatch>();
        private static readonly Stack<string> _currentKey = new Stack<string>();

        internal static int PatchedMethodCount;

        internal static void Record(string key, long elapsedTicks)
        {
            Stats s;
            _stats.TryGetValue(key, out s);
            s.TotalTicks += elapsedTicks;
            if (elapsedTicks > s.MaxTicks) s.MaxTicks = elapsedTicks;
            s.CallCount++;
            _stats[key] = s;
        }

        internal static Stopwatch Borrow()
        {
            if (_swPool.Count > 0)
            {
                var s = _swPool.Pop();
                s.Reset();
                s.Start();
                return s;
            }
            return Stopwatch.StartNew();
        }

        internal static void Return(Stopwatch sw)
        {
            if (_swPool.Count < 32) _swPool.Push(sw);
        }

        internal static void DumpAndReset()
        {
            Plugin.Logger.LogMessage($"[uprof] DumpAndReset() called, patched {PatchedMethodCount} methods, recorded {_stats.Count} keys");
            if (_stats.Count == 0)
            {
                Plugin.Logger.LogMessage("[uprof] no Update callbacks recorded.");
                return;
            }

            List<KeyValuePair<string, Stats>> snapshot;
            try
            {
                snapshot = new List<KeyValuePair<string, Stats>>(_stats);
                _stats.Clear();
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError("[uprof] snapshot failed: " + e);
                return;
            }

            snapshot.Sort((a, b) => b.Value.TotalTicks.CompareTo(a.Value.TotalTicks));
            int top = Math.Min(25, snapshot.Count);
            var sb = new System.Text.StringBuilder();
            sb.Append("[uprof] hot Update methods (total_ms / calls / max_ms / avg_us):\n");
            double ticksPerMs = Stopwatch.Frequency / 1000.0;
            double ticksPerUs = Stopwatch.Frequency / 1000000.0;
            for (int i = 0; i < top; i++)
            {
                var k = snapshot[i].Key;
                var s = snapshot[i].Value;
                double totalMs = s.TotalTicks / ticksPerMs;
                double maxMs = s.MaxTicks / ticksPerMs;
                double avgUs = s.CallCount > 0 ? (s.TotalTicks / ticksPerUs) / s.CallCount : 0;
                sb.Append($"  {k,-60} {totalMs,8:F1} / {s.CallCount,7} / {maxMs,7:F1} / {avgUs,7:F1}\n");
            }
            Plugin.Logger.LogMessage(sb.ToString());
        }

        // The shared prefix/postfix that wraps every patched method
        internal static class SharedPatch
        {
            // __originalMethod is supplied by Harmony as the MethodBase of the patched target.
            // We use it to derive the type+method name for aggregation.
            [HarmonyPrefix]
            public static void Pre(MethodBase __originalMethod, out long __state)
            {
                __state = Stopwatch.GetTimestamp();
            }

            [HarmonyPostfix]
            public static void Post(MethodBase __originalMethod, long __state)
            {
                long elapsed = Stopwatch.GetTimestamp() - __state;
                string key;
                if (!KeyMap.TryGetValue(__originalMethod, out key))
                {
                    // Fallback (shouldn't happen — we pre-populate at install time)
                    key = __originalMethod.DeclaringType.Name + "." + __originalMethod.Name;
                    KeyMap[__originalMethod] = key;
                }
                Record(key, elapsed);
            }
        }

        // Iterate Assembly-CSharp types; find MonoBehaviour subclasses that DECLARE
        // Update/LateUpdate/FixedUpdate. Patch each with the shared timer.
        internal static void InstallPatches(Harmony harmony)
        {
            var mbType = typeof(MonoBehaviour);
            var methodNames = new[] { "Update", "LateUpdate", "FixedUpdate" };
            var bindFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

            var prefix = new HarmonyMethod(typeof(SharedPatch).GetMethod("Pre", BindingFlags.Static | BindingFlags.Public));
            var postfix = new HarmonyMethod(typeof(SharedPatch).GetMethod("Post", BindingFlags.Static | BindingFlags.Public));

            int patched = 0;
            int failed = 0;
            int monoBehaviourCount = 0;
            int methodsFound = 0;
            int totalTypes = 0;

            // Walk every loaded assembly that isn't BCL/Unity/our-own
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            Plugin.Logger.LogInfo($"[uprof] scanning {assemblies.Length} loaded assemblies");

            for (int ai = 0; ai < assemblies.Length; ai++)
            {
                var asm = assemblies[ai];
                string asmName;
                try { asmName = asm.GetName().Name; } catch { continue; }
                if (asmName.StartsWith("System") || asmName.StartsWith("mscorlib") ||
                    asmName.StartsWith("Mono.") || asmName.StartsWith("UnityEngine") ||
                    asmName.StartsWith("Unity.") || asmName.StartsWith("BepInEx") ||
                    asmName.StartsWith("0Harmony") || asmName.StartsWith("HarmonyX") ||
                    asmName.StartsWith("MonoMod") || asmName == "RedAllianceSpeedrun")
                    continue;

                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types; }
                catch { continue; }

                int asmTypes = 0, asmMB = 0, asmMethods = 0, asmPatched = 0;
                for (int ti = 0; ti < types.Length; ti++)
                {
                    var t = types[ti];
                    if ((object)t == null) continue;
                    asmTypes++;
                    if (!mbType.IsAssignableFrom(t)) continue;
                    if (t.IsAbstract) continue;
                    asmMB++;

                    for (int mi = 0; mi < methodNames.Length; mi++)
                    {
                        MethodInfo m;
                        try { m = t.GetMethod(methodNames[mi], bindFlags); }
                        catch { continue; }
                        if ((object)m == null) continue;
                        if (!ReferenceEquals(m.ReturnType, typeof(void))) continue;
                        if (m.GetParameters().Length != 0) continue;
                        asmMethods++;

                        try
                        {
                            harmony.Patch(m, prefix, postfix);
                            // Pre-cache the key string so the hot path is alloc-free.
                            KeyMap[m] = t.Name + "." + methodNames[mi];
                            asmPatched++;
                        }
                        catch (Exception e)
                        {
                            failed++;
                            if (failed < 5)
                                Plugin.Logger.LogWarning($"[uprof] patch failed for {t.Name}.{methodNames[mi]}: {e.Message}");
                        }
                    }
                }

                totalTypes += asmTypes;
                monoBehaviourCount += asmMB;
                methodsFound += asmMethods;
                patched += asmPatched;

                if (asmTypes > 0)
                    Plugin.Logger.LogInfo($"[uprof]   asm '{asmName}': {asmTypes} types, {asmMB} MonoBehaviour, {asmMethods} target methods, {asmPatched} patched");
            }

            PatchedMethodCount = patched;
            Plugin.Logger.LogInfo($"[uprof] TOTAL: {totalTypes} types scanned, {monoBehaviourCount} MonoBehaviour, {methodsFound} target methods found, {patched} patched (failed: {failed})");
        }
    }
}
