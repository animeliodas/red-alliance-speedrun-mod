using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.SceneManagement;

namespace RedAllianceSpeedrun
{
    [BepInPlugin(GUID, "Red Alliance Speedrun Tools", "0.18.1")]
    public class Plugin : BaseUnityPlugin
    {
        public const string GUID = "redalliance.speedrun";

        internal static Plugin Instance;
        internal new static ManualLogSource Logger;

        private ConfigEntry<KeyCode> _restartKey;
        private ConfigEntry<KeyCode> _diagKey;
        private ConfigEntry<KeyCode> _deepDiagKey;
        private ConfigEntry<KeyCode> _profilerDumpKey;
        private ConfigEntry<bool> _invokeRepeatingProfilerEnabled;
        private ConfigEntry<bool> _updateProfilerEnabled;
        private ConfigEntry<bool> _diagOnReload;
        private ConfigEntry<bool> _restartInMenu;
        private ConfigEntry<bool> _leakFixEnabled;
        private ConfigEntry<int> _leakFixDelayFrames;
        private ConfigEntry<bool> _consoleLogLeakFix;
        private ConfigEntry<bool> _aggressiveGcOnLoad;
        private ConfigEntry<bool> _disposeOrphanSteamCallbacks;

        private bool _devConsoleUnsubscribed;

        // Cached references to current NetworkManager's Steam callbacks. We hold these alive
        // ourselves to prevent finalizer races, and Dispose them explicitly when a new
        // NetworkManager creates fresh ones. This eliminates pinned-GCHandle accumulation
        // that fragments the Mono heap across Transfer-mode restarts.
        internal static IDisposable s_prevOverlayCallback;
        internal static IDisposable s_prevPlayerCountCallback;
        internal static int s_steamCallbackDisposeCount;

        // FPS sample
        private float _fpsAccum;
        private int _fpsFrames;
        private float _fpsLastTime;
        private float _lastFps;

        // Frame-time spike tracker: collect frame durations in a ring, report max + p99
        // over the past ~2 seconds.
        private const int FrameWindow = 240;
        private readonly float[] _frameMs = new float[FrameWindow];
        private int _frameIdx;
        private int _frameFilled;
        private float _lastFrameMaxMs;
        private float _lastFrameP99Ms;
        private int _lastFrameSpikeCount; // frames > 33ms in the window (= dropped from 30fps)

        // Per-spike trace: log every frame longer than _spikeLogThresholdMs with relative
        // time since last scene load and current restart count.
        private ConfigEntry<float> _spikeLogThresholdMs;
        private float _lastSceneLoadTime;
        private int _restartCount;
        private int _frameInWindow;

        // Allocation-rate sample (KB/sec, derived from Profiler.GetMonoUsedSizeLong deltas)
        private float _allocLastSampleTime;
        private long _allocLastBytes;
        private float _lastAllocKbPerSec;
        private float _lastGcPerSec;
        private int _lastGcCount;

        // Log-message-rate sample (count/sec, via our own Application.logMessageReceived hook)
        private int _logCountThisWindow;
        private int _lastLogsPerSec;

        private float _lastRestartTime;
        private const float RestartDebounce = 0.5f;

        private void Awake()
        {
            Instance = this;
            Logger = base.Logger;

            _restartKey = Config.Bind(
                "Hotkeys", "RestartKey", KeyCode.Tab,
                "Reloads the current scene (quick run restart). Default: Tab.");
            _diagKey = Config.Bind(
                "Hotkeys", "DiagnosticsKey", KeyCode.F11,
                "Prints memory diagnostics to BepInEx/LogOutput.log.");
            _deepDiagKey = Config.Bind(
                "Hotkeys", "DeepDiagnosticsKey", KeyCode.F12,
                "Prints detailed diagnostics (loaded scenes, top GameObject names, Profiler memory).");
            _profilerDumpKey = Config.Bind(
                "Hotkeys", "ProfilerDumpKey", KeyCode.F9,
                "Dumps the InvokeRepeating profiler stats: which periodic callbacks have eaten the most CPU since last dump. Resets counters after each dump. (F10 is sometimes captured by the OS, F9 is safer.)");
            _invokeRepeatingProfilerEnabled = Config.Bind(
                "Diagnostics", "InvokeRepeatingProfiler", true,
                "Time every call to common ~1-second InvokeRepeating callbacks (GlobalAIScript, LightDistanceCullingScript, ObjectDisableScript, FootStepsScriptNew, AILightHeightOptimizationScript). F11 dumps top consumers.");
            _updateProfilerEnabled = Config.Bind(
                "Diagnostics", "UpdateProfiler", true,
                "Patch Update/LateUpdate/FixedUpdate of EVERY game MonoBehaviour subclass with a Stopwatch timer. Heavy (patches 50-100+ methods) but reveals which per-frame method actually takes time. F11 dumps top consumers.");
            _diagOnReload = Config.Bind(
                "Diagnostics", "LogOnLevelLoad", true,
                "Log RT/material/audio/DDOL counts after every scene load.");
            _restartInMenu = Config.Bind(
                "Hotkeys", "RestartInMenu", false,
                "If false, the restart key is ignored when the active scene is main_menu, credits, or start_screen.");
            _leakFixEnabled = Config.Bind(
                "LeakFix", "Enabled", true,
                "Sweep orphan post-effect materials after every scene load. Fixes the " +
                "+4-materials-per-restart leak caused by PostEffectBaseNew / GlobalFog / " +
                "Vignetting / NoiseAndGrain / ColorCorrectionCurves / Antialiasing scripts " +
                "flagging their materials DontUnloadUnusedAsset without an OnDisable cleanup.");
            _leakFixDelayFrames = Config.Bind(
                "LeakFix", "DelayFrames", 2,
                "How many frames to wait after sceneLoaded before sweeping. " +
                "Lets the new scene's Awake/Start run so live materials are referenced.");
            _consoleLogLeakFix = Config.Bind(
                "LeakFix", "UnsubscribeDevConsoleLog", true,
                "Unsubscribe DeveloperConsoleScript.HandleLog from Application.logMessageReceived " +
                "and clear its consoleMessages list on every scene load. Eliminates a growing " +
                "static list that accumulates ~6 Debug.Log messages per restart.");
            _aggressiveGcOnLoad = Config.Bind(
                "LeakFix", "AggressiveGCOnLoad", true,
                "After each scene load, force a full GC + WaitForPendingFinalizers + a second GC. " +
                "The game's own LoadLevel calls GC.Collect but not WaitForPendingFinalizers, so " +
                "orphaned objects with finalizers (Steam Callback<T>, IDisposables) may persist " +
                "in pinned state across restarts and fragment the Mono heap, increasing GC " +
                "frequency over time. This forces the cleanup synchronously.");
            _spikeLogThresholdMs = Config.Bind(
                "Diagnostics", "SpikeLogThresholdMs", 50f,
                "Log every individual frame whose duration exceeds this many ms, with timing " +
                "relative to the last scene load. Set to 0 to disable.");
            _disposeOrphanSteamCallbacks = Config.Bind(
                "LeakFix", "DisposeOrphanSteamCallbacks", true,
                "Hook NetworkManager.OnEnable to explicitly Dispose the previous NetworkManager's " +
                "Steam Callback<T> objects when a new NetworkManager replaces it. The game itself " +
                "relies on Mono's finalizer to clean these up, but on a fragmented heap the " +
                "finalizer can lag, leaving pinned GCHandles that further fragment the heap and " +
                "accelerate GC frequency. Explicit disposal eliminates this source of leakage.");

            SceneManager.sceneLoaded += OnSceneLoaded;
            Application.logMessageReceived += OnLogCount;

            if (_disposeOrphanSteamCallbacks.Value)
            {
                try
                {
                    var harmony = new Harmony(GUID);
                    harmony.PatchAll(typeof(NetworkManagerSteamCallbackPatch));
                    Logger.LogInfo("[steamfix] Harmony patch installed on NetworkManager.OnEnable.");
                }
                catch (Exception e)
                {
                    Logger.LogError("[steamfix] Failed to install Harmony patch: " + e);
                }
            }

            if (_invokeRepeatingProfilerEnabled.Value)
            {
                try
                {
                    var harmony = new Harmony(GUID + ".profiler");
                    harmony.PatchAll(typeof(Patch_GetDistanceToPlayerCamera));
                    harmony.PatchAll(typeof(Patch_ForceUpdateTarget));
                    harmony.PatchAll(typeof(Patch_UpdateMeshShadows));
                    harmony.PatchAll(typeof(Patch_UpdateTargetCondition));
                    harmony.PatchAll(typeof(Patch_GetClosestTarget));
                    harmony.PatchAll(typeof(Patch_CheckDistance));
                    harmony.PatchAll(typeof(Patch_DisableCheck));
                    harmony.PatchAll(typeof(Patch_DistanceCheck));
                    harmony.PatchAll(typeof(Patch_CheckHeight));
                    Logger.LogInfo("[profiler] Harmony patches installed on 9 InvokeRepeating callbacks. F11 dumps.");
                }
                catch (Exception e)
                {
                    Logger.LogError("[profiler] Failed to install Harmony patches: " + e);
                }
            }

            if (_updateProfilerEnabled.Value)
            {
                try
                {
                    var harmony = new Harmony(GUID + ".uprof");
                    UpdateProfiler.InstallPatches(harmony);
                }
                catch (Exception e)
                {
                    Logger.LogError("[uprof] Failed to install update profiler: " + e);
                }
            }

            // InputManager.Update optimization disabled in v0.17.1 — broke player movement
            // in v0.17.0. Will revisit with a non-replacement approach later.

            // WaterPhysicsScript.FloatObjects replacement — opt-in
            var waterOpt = Config.Bind(
                "Optimizations", "PatchWaterPhysics", false,
                "Replace WaterPhysicsScript.FloatObjects with an alloc-free, O(N) version. " +
                "The original uses LINQ ElementAt(i) which is O(N²) and allocates per call. " +
                "Test carefully — physics behaviour should match but if anything looks off, set false.");
            if (waterOpt.Value)
            {
                try
                {
                    var harmony = new Harmony(GUID + ".waterfix");
                    harmony.PatchAll(typeof(WaterPhysicsFloatObjectsPatch));
                    Logger.LogInfo("[waterfix] Patched WaterPhysicsScript.FloatObjects.");
                }
                catch (Exception e)
                {
                    Logger.LogError("[waterfix] Failed to patch: " + e);
                }
            }

            Logger.LogInfo($"Restart key: {_restartKey.Value}; Diag key: {_diagKey.Value}; LeakFix: {_leakFixEnabled.Value}");
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            Application.logMessageReceived -= OnLogCount;
        }

        private void OnLogCount(string _, string __, LogType ___)
        {
            // Just count — don't allocate. Cheap counter to detect log-spam growth.
            _logCountThisWindow++;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (mode != LoadSceneMode.Single) return;

            // Reset spike-log timing baseline. Increment restart counter for any non-startup scene.
            _lastSceneLoadTime = Time.unscaledTime;
            _frameInWindow = 0;
            if (scene.name != "start_screen" && scene.name != "object_pool" &&
                scene.name != "main_menu" && scene.name != "credits")
            {
                _restartCount++;
            }

            if (_consoleLogLeakFix != null && _consoleLogLeakFix.Value)
            {
                ApplyDevConsoleLogLeakFix();
            }

            if (_aggressiveGcOnLoad != null && _aggressiveGcOnLoad.Value)
            {
                ApplyAggressiveGC();
            }

            // Don't touch early-startup scenes: anything we sweep here can take the loading
            // screen / main menu transition down before the player ever sees gameplay.
            bool earlyScene =
                scene.name == "start_screen" ||
                scene.name == "object_pool" ||
                scene.name == "main_menu" ||
                scene.name == "credits";
            if (!earlyScene && _leakFixEnabled != null && _leakFixEnabled.Value)
            {
                StartCoroutine(SweepAfterLoad(scene.name));
            }
            else if (_diagOnReload != null && _diagOnReload.Value)
            {
                LogDiagnostics("post-load:" + scene.name);
            }
        }

        private void ApplyAggressiveGC()
        {
            try
            {
                long beforeMb = GC.GetTotalMemory(false) / (1024L * 1024L);
                int beforeGc = GC.CollectionCount(0);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                Resources.UnloadUnusedAssets();
                sw.Stop();
                long afterMb = GC.GetTotalMemory(false) / (1024L * 1024L);
                int afterGc = GC.CollectionCount(0);
                Logger.LogInfo($"[gcfix] {sw.ElapsedMilliseconds}ms  mono {beforeMb}->{afterMb}MB  gc+={afterGc - beforeGc}");
            }
            catch (Exception e)
            {
                Logger.LogError("Aggressive GC failed: " + e);
            }
        }

        private void ApplyDevConsoleLogLeakFix()
        {
            try
            {
                if (!_devConsoleUnsubscribed)
                {
                    // Application.logMessageReceived -= DeveloperConsoleScript.HandleLog
                    // Done once. Static field `invokedConsoleReading` stays true so the game's
                    // own Start() will not re-subscribe.
                    Application.logMessageReceived -= DeveloperConsoleScript.HandleLog;
                    _devConsoleUnsubscribed = true;
                    Logger.LogInfo("[consolefix] HandleLog unsubscribed from Application.logMessageReceived.");
                }

                // Clear the static consoleMessages list every load. Reflection because field is private.
                var f = typeof(DeveloperConsoleScript).GetField("consoleMessages",
                    BindingFlags.Static | BindingFlags.NonPublic);
                if ((object)f != null)
                {
                    var list = f.GetValue(null) as System.Collections.IList;
                    if (list != null && list.Count > 0)
                    {
                        int before = list.Count;
                        list.Clear();
                        Logger.LogInfo($"[consolefix] consoleMessages cleared ({before} entries).");
                    }
                }
            }
            catch (Exception e)
            {
                Logger.LogError("Dev console log leak fix failed: " + e);
            }
        }

        private IEnumerator SweepAfterLoad(string sceneName)
        {
            int wait = Math.Max(0, _leakFixDelayFrames.Value);
            for (int i = 0; i < wait; i++) yield return null;

            int cleared = 0;
            int destroyed = 0;
            try
            {
                int before = Resources.FindObjectsOfTypeAll<Material>().Length;

                var mats = Resources.FindObjectsOfTypeAll<Material>();
                for (int i = 0; i < mats.Length; i++)
                {
                    var m = mats[i];
                    if (m == null) continue;
                    if (!IsLeakCandidate(m)) continue;
                    if ((m.hideFlags & HideFlags.DontUnloadUnusedAsset) == 0) continue;
                    m.hideFlags &= ~HideFlags.DontUnloadUnusedAsset;
                    cleared++;
                }

                if (cleared > 0)
                {
                    var op = Resources.UnloadUnusedAssets();
                    while (!op.isDone) yield return null;
                    int after = Resources.FindObjectsOfTypeAll<Material>().Length;
                    destroyed = before - after;
                }
            }
            finally
            {
                if (cleared > 0)
                    Logger.LogInfo($"[leakfix] scene='{sceneName}'  flagged={cleared}  freed={destroyed}");
            }

            if (_diagOnReload != null && _diagOnReload.Value)
                LogDiagnostics("post-load:" + sceneName);
        }

        // Narrow allowlist — only the post-effect shaders fed through PostEffectBaseNew
        // and similar leak-prone helpers. Other "Hidden/" shaders (decals, particles,
        // skybox, UI internals) are gameplay-critical and must not be touched.
        private static readonly string[] LeakShaderSubstrings = new[]
        {
            "Hidden/ColorCorrectionCurves",
            "Hidden/ColorCorrectionCurvesSimple",
            "Hidden/ColorCorrectionSelective",
            "Hidden/VignettingShader",
            "Hidden/Vignetting",
            "Hidden/SeparableBlur",
            "Hidden/ChromaticAberration",
            "Hidden/NoiseAndGrain",
            "Hidden/FXAAPreset",
            "Hidden/FXAA",
            "Hidden/NFAA",
            "Hidden/SSAA",
            "Hidden/DLAA",
            "Hidden/GlobalFog",
            "Hidden/CameraMotionBlur",
        };

        private static bool IsLeakCandidate(Material m)
        {
            var s = m.shader;
            if (s == null) return false;
            var name = s.name;
            if (string.IsNullOrEmpty(name)) return false;
            for (int i = 0; i < LeakShaderSubstrings.Length; i++)
            {
                if (name.IndexOf(LeakShaderSubstrings[i], StringComparison.Ordinal) >= 0)
                    return true;
            }
            return false;
        }

        private int _lastGcSampleCount;

        private void Update()
        {
            // Per-frame time, captured in a ring buffer
            float frameMs = Time.unscaledDeltaTime * 1000f;
            _frameMs[_frameIdx] = frameMs;
            _frameIdx = (_frameIdx + 1) % FrameWindow;
            if (_frameFilled < FrameWindow) _frameFilled++;
            _frameInWindow++;

            // Track GC delta for this frame
            int gcNowSample = GC.CollectionCount(0);
            int gcDelta = gcNowSample - _lastGcSampleCount;
            _lastGcSampleCount = gcNowSample;

            // Per-spike trace: log individual frames that exceed the threshold
            float spikeThreshold = _spikeLogThresholdMs != null ? _spikeLogThresholdMs.Value : 50f;
            if (spikeThreshold > 0f && frameMs > spikeThreshold)
            {
                float msSinceLoad = (Time.unscaledTime - _lastSceneLoadTime) * 1000f;
                Logger.LogMessage(
                    $"[spike] restart#{_restartCount}  {msSinceLoad:F0}ms post-load  frame#{_frameInWindow}  duration={frameMs:F1}ms  gcInFrame={gcDelta}");
            }

            // Running FPS sample, refreshed once per second.
            _fpsAccum += Time.unscaledDeltaTime;
            _fpsFrames++;
            if (_fpsAccum - _fpsLastTime >= 1f)
            {
                _lastFps = _fpsFrames / (_fpsAccum - _fpsLastTime);
                _fpsLastTime = _fpsAccum;
                _fpsFrames = 0;
                RecomputeFrameStats();
            }

            // Allocation rate + GC rate + log rate, refreshed once per second.
            float now = Time.unscaledTime;
            if (now - _allocLastSampleTime >= 1f)
            {
                long monoNow = 0;
                try { monoNow = Profiler.GetMonoUsedSizeLong(); } catch { }
                int gcNow = GC.CollectionCount(0);

                if (_allocLastSampleTime > 0f)
                {
                    float dt = now - _allocLastSampleTime;
                    long delta = monoNow - _allocLastBytes;
                    // If GC ran during this window, the delta can be negative; clamp.
                    if (delta < 0) delta = 0;
                    _lastAllocKbPerSec = (delta / 1024f) / dt;
                    _lastGcPerSec = (gcNow - _lastGcCount) / dt;
                    _lastLogsPerSec = (int)(_logCountThisWindow / dt);
                }

                _allocLastBytes = monoNow;
                _allocLastSampleTime = now;
                _lastGcCount = gcNow;
                _logCountThisWindow = 0;
            }

            if (Input.GetKeyDown(_restartKey.Value))
            {
                TryQuickRestart();
            }
            if (Input.GetKeyDown(_diagKey.Value))
            {
                LogDiagnostics("manual");
                try { InvokeRepeatingProfiler.DumpAndReset(); }
                catch (Exception e) { Logger.LogError("[diag] InvokeRepeatingProfiler.DumpAndReset threw: " + e); }
                try { UpdateProfiler.DumpAndReset(); }
                catch (Exception e) { Logger.LogError("[diag] UpdateProfiler.DumpAndReset threw: " + e); }
            }
            if (Input.GetKeyDown(_deepDiagKey.Value))
            {
                LogDeepDiagnostics("manual");
            }
            if (Input.GetKeyDown(_profilerDumpKey.Value))
            {
                InvokeRepeatingProfiler.DumpAndReset();
            }
        }

        private void TryQuickRestart()
        {
            if (Time.unscaledTime - _lastRestartTime < RestartDebounce)
                return;
            _lastRestartTime = Time.unscaledTime;

            if (SceneManagerScript.LoadingLevel)
            {
                Logger.LogInfo("Restart ignored: already loading.");
                return;
            }

            var active = SceneManager.GetActiveScene().name;
            if (!_restartInMenu.Value && (active == "main_menu" || active == "credits" || active == "start_screen"))
            {
                Logger.LogInfo("Restart ignored: in menu scene '" + active + "'.");
                return;
            }

            var smgr = SceneManagerScript.Instance;
            if (smgr == null)
            {
                Logger.LogWarning("Restart failed: SceneManagerScript.Instance is null.");
                return;
            }

            var loadType = (active == "main_menu" || active == "credits")
                ? LevelLoadingType.Reset
                : LevelLoadingType.Transfer;

            Logger.LogInfo($"Quick restart -> '{active}' (loadType={loadType}).");
            try { NetworkManager.OnStartedNewGame(); }
            catch (Exception e) { Logger.LogWarning("OnStartedNewGame threw: " + e.Message); }
            smgr.StartCoroutine(smgr.LoadLevel(active, 0f, false, Vector3.zero, Vector3.zero, loadType));
        }

        internal void LogDiagnostics(string tag)
        {
            try
            {
                int rt = Resources.FindObjectsOfTypeAll<RenderTexture>().Length;
                int mats = Resources.FindObjectsOfTypeAll<Material>().Length;
                int meshes = Resources.FindObjectsOfTypeAll<Mesh>().Length;
                int texs = Resources.FindObjectsOfTypeAll<Texture>().Length;
                int audio = Resources.FindObjectsOfTypeAll<AudioSource>().Length;
                int particles = Resources.FindObjectsOfTypeAll<ParticleSystem>().Length;
                int cams = Resources.FindObjectsOfTypeAll<Camera>().Length;
                int allGO = Resources.FindObjectsOfTypeAll<GameObject>().Length;
                int allComponents = Resources.FindObjectsOfTypeAll<Component>().Length;
                int allBehaviours = Resources.FindObjectsOfTypeAll<MonoBehaviour>().Length;

                int ddol = CountDontDestroyOnLoad();
                long monoMb = GC.GetTotalMemory(false) / (1024L * 1024L);
                long totalReservedMb = Profiler.GetTotalReservedMemoryLong() / (1024L * 1024L);
                long totalAllocatedMb = Profiler.GetTotalAllocatedMemoryLong() / (1024L * 1024L);
                long monoUsedMb = Profiler.GetMonoUsedSizeLong() / (1024L * 1024L);

                int olwl = OnLevelWasLoadedSubscriberCount();
                int playingAudio = CountPlayingAudio();
                int enabledMB = CountEnabledMonoBehaviours();

                int gc0 = GC.CollectionCount(0);
                int gc1 = GC.CollectionCount(1);
                int gc2 = GC.CollectionCount(2);

                int handles = 0, threads = 0;
                try
                {
                    using (var proc = System.Diagnostics.Process.GetCurrentProcess())
                    {
                        handles = proc.HandleCount;
                        threads = proc.Threads.Count;
                    }
                }
                catch { }

                Logger.LogMessage(
                    $"[diag {tag}] fps={_lastFps:F1}  frameMax={_lastFrameMaxMs:F1}ms  frameP99={_lastFrameP99Ms:F1}ms  spikes>33ms={_lastFrameSpikeCount}  alloc={_lastAllocKbPerSec:F0}KB/s  gc/s={_lastGcPerSec:F1}  logs/s={_lastLogsPerSec}  GC0={gc0} GC1={gc1} GC2={gc2}  monoGC={monoMb}MB  nativeAlloc={totalAllocatedMb}MB  reserved={totalReservedMb}MB  RT={rt}  Mat={mats}  Tex={texs}  Mesh={meshes}  AudioSrc={audio}  PSAudPlay={playingAudio}  PS={particles}  Cam={cams}  GO={allGO}  Comp={allComponents}  MB={allBehaviours}  MBon={enabledMB}  OnLevelWasLoaded={olwl}  DDOL={ddol}");
            }
            catch (Exception e)
            {
                Logger.LogError("Diagnostics failed: " + e);
            }
        }

        internal void LogDeepDiagnostics(string tag)
        {
            try
            {
                // Loaded scenes
                int sceneCount = SceneManager.sceneCount;
                var sceneNames = new List<string>(sceneCount);
                for (int i = 0; i < sceneCount; i++)
                {
                    var s = SceneManager.GetSceneAt(i);
                    sceneNames.Add($"{s.name}({(s.isLoaded ? "L" : "-")},rgo={(s.isLoaded ? s.rootCount : 0)})");
                }
                Logger.LogMessage($"[deep {tag}] scenes({sceneCount}): " + string.Join(", ", sceneNames.ToArray()));

                // Top GameObject names by count (most likely to reveal growing pools)
                var counts = new Dictionary<string, int>(2048);
                var allGOs = Resources.FindObjectsOfTypeAll<GameObject>();
                for (int i = 0; i < allGOs.Length; i++)
                {
                    var n = allGOs[i].name;
                    int c;
                    counts.TryGetValue(n, out c);
                    counts[n] = c + 1;
                }
                var sorted = new List<KeyValuePair<string, int>>(counts);
                sorted.Sort((a, b) => b.Value.CompareTo(a.Value));
                int top = Math.Min(20, sorted.Count);
                var sb = new System.Text.StringBuilder();
                sb.Append($"[deep {tag}] top GO names: ");
                for (int i = 0; i < top; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(sorted[i].Key).Append('=').Append(sorted[i].Value);
                }
                Logger.LogMessage(sb.ToString());

                // Top component types by count
                var compCounts = new Dictionary<string, int>(512);
                var allComps = Resources.FindObjectsOfTypeAll<Component>();
                for (int i = 0; i < allComps.Length; i++)
                {
                    if (allComps[i] == null) continue;
                    var n = allComps[i].GetType().Name;
                    int c;
                    compCounts.TryGetValue(n, out c);
                    compCounts[n] = c + 1;
                }
                var sortedComps = new List<KeyValuePair<string, int>>(compCounts);
                sortedComps.Sort((a, b) => b.Value.CompareTo(a.Value));
                int topC = Math.Min(20, sortedComps.Count);
                var sb2 = new System.Text.StringBuilder();
                sb2.Append($"[deep {tag}] top components: ");
                for (int i = 0; i < topC; i++)
                {
                    if (i > 0) sb2.Append(", ");
                    sb2.Append(sortedComps[i].Key).Append('=').Append(sortedComps[i].Value);
                }
                Logger.LogMessage(sb2.ToString());

                // DDOL roots — print all of them
                var ddolNames = ListDontDestroyOnLoadRoots();
                Logger.LogMessage($"[deep {tag}] DDOL roots: " + string.Join(", ", ddolNames.ToArray()));

                // Full static delegate scan — heaviest, but catches leaks invisible to object counters
                LogDelegateLeakCandidates(tag);

                // Full static collection scan — catches static List/Dict/HashSet that accumulate
                LogCollectionLeakCandidates(tag);

                // Instance-level delegate scan: walks every live Component and aggregates
                // invocation-list lengths of delegate fields, grouped by type.field. Heavy.
                LogInstanceDelegateLeakCandidates(tag);

                // Instance-level collection scan: sums Count of every collection-typed field
                // across all live components of each type. Catches growing private List/Dict
                // on game scripts that accumulate state across restarts.
                LogInstanceCollectionLeakCandidates(tag);
            }
            catch (Exception e)
            {
                Logger.LogError("Deep diagnostics failed: " + e);
            }
        }

        // Reflectively count subscribers of SceneManagerScript.OnLevelWasLoaded.
        // The compiler emits the event as a private static delegate field with the same name.
        private static int OnLevelWasLoadedSubscriberCount()
        {
            try
            {
                var t = typeof(SceneManagerScript);
                var f = t.GetField("OnLevelWasLoaded",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                if ((object)f == null) return -1;
                var d = f.GetValue(null) as Delegate;
                if ((object)d == null) return 0;
                return d.GetInvocationList().Length;
            }
            catch { return -2; }
        }

        private static int CountPlayingAudio()
        {
            try
            {
                var all = Resources.FindObjectsOfTypeAll<AudioSource>();
                int n = 0;
                for (int i = 0; i < all.Length; i++)
                {
                    var a = all[i];
                    if (a != null && a.isPlaying) n++;
                }
                return n;
            }
            catch { return -1; }
        }

        private static int CountEnabledMonoBehaviours()
        {
            try
            {
                var all = Resources.FindObjectsOfTypeAll<MonoBehaviour>();
                int n = 0;
                for (int i = 0; i < all.Length; i++)
                {
                    var b = all[i];
                    if (b != null && b.isActiveAndEnabled) n++;
                }
                return n;
            }
            catch { return -1; }
        }

        // Walk all loaded assemblies; for every static delegate-typed field that's non-null,
        // print its invocation list length. Catches leaks of `event` / Action / Func / delegate
        // fields anywhere in the game's code. Heavy — only run on demand via F12.
        internal void LogDelegateLeakCandidates(string tag)
        {
            var report = new List<KeyValuePair<string, int>>();
            try
            {
                var assemblies = AppDomain.CurrentDomain.GetAssemblies();
                for (int ai = 0; ai < assemblies.Length; ai++)
                {
                    var asm = assemblies[ai];
                    string asmName;
                    try { asmName = asm.GetName().Name; } catch { continue; }
                    // Skip BCL / Unity engine / our own / BepInEx noise
                    if (asmName.StartsWith("System") ||
                        asmName.StartsWith("mscorlib") ||
                        asmName.StartsWith("Mono.") ||
                        asmName.StartsWith("UnityEngine") ||
                        asmName.StartsWith("Unity.") ||
                        asmName.StartsWith("BepInEx") ||
                        asmName.StartsWith("0Harmony") ||
                        asmName.StartsWith("HarmonyX") ||
                        asmName.StartsWith("MonoMod") ||
                        asmName == "RedAllianceSpeedrun")
                        continue;

                    Type[] types;
                    try { types = asm.GetTypes(); }
                    catch (ReflectionTypeLoadException e) { types = e.Types; }
                    catch { continue; }

                    for (int ti = 0; ti < types.Length; ti++)
                    {
                        var t = types[ti];
                        if ((object)t == null) continue;
                        FieldInfo[] fields;
                        try
                        {
                            fields = t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                        }
                        catch { continue; }
                        for (int fi = 0; fi < fields.Length; fi++)
                        {
                            var f = fields[fi];
                            if ((object)f == null) continue;
                            if (!typeof(Delegate).IsAssignableFrom(f.FieldType)) continue;
                            object v;
                            try { v = f.GetValue(null); } catch { continue; }
                            var d = v as Delegate;
                            if ((object)d == null) continue;
                            int len;
                            try { len = d.GetInvocationList().Length; } catch { continue; }
                            if (len <= 0) continue;
                            report.Add(new KeyValuePair<string, int>(t.FullName + "." + f.Name, len));
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Logger.LogError("Delegate scan failed: " + e);
            }

            report.Sort((a, b) => b.Value.CompareTo(a.Value));
            var sb = new System.Text.StringBuilder();
            sb.Append($"[deleg {tag}] live static delegates: ");
            int n = Math.Min(report.Count, 40);
            for (int i = 0; i < n; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(report[i].Key).Append('=').Append(report[i].Value);
            }
            if (n == 0) sb.Append("(none)");
            Logger.LogMessage(sb.ToString());
        }

        // Walk all static fields; for every collection-typed one, print its Count.
        // Run on F12. Will surface any static List/Dict/HashSet that accumulates items across restarts.
        internal void LogCollectionLeakCandidates(string tag)
        {
            var report = new List<KeyValuePair<string, int>>();
            try
            {
                var assemblies = AppDomain.CurrentDomain.GetAssemblies();
                for (int ai = 0; ai < assemblies.Length; ai++)
                {
                    var asm = assemblies[ai];
                    string asmName;
                    try { asmName = asm.GetName().Name; } catch { continue; }
                    if (asmName.StartsWith("System") ||
                        asmName.StartsWith("mscorlib") ||
                        asmName.StartsWith("Mono.") ||
                        asmName.StartsWith("UnityEngine") ||
                        asmName.StartsWith("Unity.") ||
                        asmName.StartsWith("BepInEx") ||
                        asmName.StartsWith("0Harmony") ||
                        asmName.StartsWith("HarmonyX") ||
                        asmName.StartsWith("MonoMod") ||
                        asmName == "RedAllianceSpeedrun")
                        continue;

                    Type[] types;
                    try { types = asm.GetTypes(); }
                    catch (ReflectionTypeLoadException e) { types = e.Types; }
                    catch { continue; }

                    for (int ti = 0; ti < types.Length; ti++)
                    {
                        var t = types[ti];
                        if ((object)t == null) continue;
                        FieldInfo[] fields;
                        try { fields = t.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic); }
                        catch { continue; }
                        for (int fi = 0; fi < fields.Length; fi++)
                        {
                            var f = fields[fi];
                            if ((object)f == null) continue;
                            var ft = f.FieldType;
                            // Skip arrays — they don't grow without explicit reassignment, and most are caches.
                            if (ft.IsArray) continue;
                            // Only generic collections — IList, IDictionary, ISet, ICollection<T>.
                            if (!IsCollectionType(ft)) continue;
                            object v;
                            try { v = f.GetValue(null); } catch { continue; }
                            if (v == null) continue;
                            int count = TryGetCount(v);
                            if (count <= 0) continue;
                            report.Add(new KeyValuePair<string, int>(t.FullName + "." + f.Name, count));
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Logger.LogError("Collection scan failed: " + e);
            }

            // Sort: biggest first, then alphabetical for stable diffing
            report.Sort((a, b) => {
                int c = b.Value.CompareTo(a.Value);
                return c != 0 ? c : string.CompareOrdinal(a.Key, b.Key);
            });
            // Print top 60 so we can spot the growing one
            int n = Math.Min(report.Count, 60);
            var sb = new System.Text.StringBuilder();
            sb.Append($"[coll {tag}] static collections (top {n}/{report.Count}): ");
            for (int i = 0; i < n; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(report[i].Key).Append('=').Append(report[i].Value);
            }
            Logger.LogMessage(sb.ToString());
        }

        // Cached typeof()s — comparing Type instances via the `==` operator would call
        // Type.op_Equality which isn't present in Unity 2017.4's Mono. Use ReferenceEquals.
        private static readonly Type T_List = typeof(List<>);
        private static readonly Type T_Dict = typeof(Dictionary<,>);
        private static readonly Type T_HashSet = typeof(HashSet<>);
        private static readonly Type T_Queue = typeof(Queue<>);
        private static readonly Type T_Stack = typeof(Stack<>);
        private static readonly Type T_LinkedList = typeof(LinkedList<>);
        private static readonly Type T_SortedList = typeof(SortedList<,>);
        private static readonly Type T_SortedDict = typeof(SortedDictionary<,>);

        private static bool IsCollectionType(Type t)
        {
            if ((object)t == null) return false;
            if (!t.IsGenericType) return false;
            Type def;
            try { def = t.GetGenericTypeDefinition(); }
            catch { return false; }
            if ((object)def == null) return false;
            return ReferenceEquals(def, T_List)
                || ReferenceEquals(def, T_Dict)
                || ReferenceEquals(def, T_HashSet)
                || ReferenceEquals(def, T_Queue)
                || ReferenceEquals(def, T_Stack)
                || ReferenceEquals(def, T_LinkedList)
                || ReferenceEquals(def, T_SortedList)
                || ReferenceEquals(def, T_SortedDict);
        }

        // Scan every live Component for instance-level delegate fields. For each field, sum
        // invocation-list lengths across all instances. Report top growers. This catches the
        // pattern where many subscribers accumulate on an `event` declared on a DDOL singleton.
        internal void LogInstanceDelegateLeakCandidates(string tag)
        {
            // key = "TypeName.FieldName", value = (total invocations, instance count with non-null)
            var totals = new Dictionary<string, int>(256);
            var instanceCounts = new Dictionary<string, int>(256);
            // Per-type field cache to avoid repeated reflection
            var fieldCache = new Dictionary<Type, FieldInfo[]>(128);

            try
            {
                var comps = Resources.FindObjectsOfTypeAll<Component>();
                for (int i = 0; i < comps.Length; i++)
                {
                    var c = comps[i];
                    if ((object)c == null) continue;
                    Type t;
                    try { t = c.GetType(); } catch { continue; }
                    if ((object)t == null) continue;

                    // Skip Unity built-in types (their delegate fields are usually internal & uninteresting)
                    var ns = t.Namespace;
                    if (ns != null && (ns.StartsWith("UnityEngine") || ns.StartsWith("TMPro") || ns.StartsWith("BepInEx")))
                        continue;

                    FieldInfo[] fields;
                    if (!fieldCache.TryGetValue(t, out fields))
                    {
                        try
                        {
                            fields = t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        }
                        catch { fields = null; }
                        // Filter to delegate-typed
                        if (fields != null)
                        {
                            var keep = new List<FieldInfo>(8);
                            for (int fi = 0; fi < fields.Length; fi++)
                            {
                                var f = fields[fi];
                                if ((object)f == null) continue;
                                if (typeof(Delegate).IsAssignableFrom(f.FieldType)) keep.Add(f);
                            }
                            fields = keep.ToArray();
                        }
                        fieldCache[t] = fields ?? new FieldInfo[0];
                    }

                    if (fields == null || fields.Length == 0) continue;

                    for (int fi = 0; fi < fields.Length; fi++)
                    {
                        var f = fields[fi];
                        object v;
                        try { v = f.GetValue(c); } catch { continue; }
                        var d = v as Delegate;
                        if ((object)d == null) continue;
                        int len;
                        try { len = d.GetInvocationList().Length; } catch { continue; }
                        if (len <= 0) continue;

                        var key = t.FullName + "." + f.Name;
                        int curT, curN;
                        totals.TryGetValue(key, out curT);
                        instanceCounts.TryGetValue(key, out curN);
                        totals[key] = curT + len;
                        instanceCounts[key] = curN + 1;
                    }
                }
            }
            catch (Exception e)
            {
                Logger.LogError("Instance delegate scan failed: " + e);
                return;
            }

            // Sort by total subscribers (suspect leaks are the biggest)
            var entries = new List<KeyValuePair<string, int>>(totals);
            entries.Sort((a, b) => {
                int c = b.Value.CompareTo(a.Value);
                return c != 0 ? c : string.CompareOrdinal(a.Key, b.Key);
            });

            int n = Math.Min(entries.Count, 60);
            var sb = new System.Text.StringBuilder();
            sb.Append($"[idel {tag}] instance delegates (top {n}/{entries.Count}, format type.field=totalSubs/instances): ");
            for (int i = 0; i < n; i++)
            {
                if (i > 0) sb.Append(", ");
                var key = entries[i].Key;
                int total = entries[i].Value;
                int inst;
                instanceCounts.TryGetValue(key, out inst);
                sb.Append(key).Append('=').Append(total).Append('/').Append(inst);
            }
            if (n == 0) sb.Append("(none)");
            Logger.LogMessage(sb.ToString());
        }

        private static int TryGetCount(object obj)
        {
            try
            {
                var p = obj.GetType().GetProperty("Count",
                    BindingFlags.Instance | BindingFlags.Public);
                if ((object)p == null) return -1;
                var v = p.GetValue(obj, null);
                if (v is int i) return i;
                return -1;
            }
            catch { return -1; }
        }

        // Same idea as instance delegate scan but for collection-typed fields. For each
        // (type, fieldName), sum Count across all instances of that component type.
        internal void LogInstanceCollectionLeakCandidates(string tag)
        {
            var totals = new Dictionary<string, int>(256);
            var instanceCounts = new Dictionary<string, int>(256);
            var fieldCache = new Dictionary<Type, FieldInfo[]>(128);

            try
            {
                var comps = Resources.FindObjectsOfTypeAll<Component>();
                for (int i = 0; i < comps.Length; i++)
                {
                    var c = comps[i];
                    if ((object)c == null) continue;
                    Type t;
                    try { t = c.GetType(); } catch { continue; }
                    if ((object)t == null) continue;

                    var ns = t.Namespace;
                    if (ns != null && (ns.StartsWith("UnityEngine") || ns.StartsWith("TMPro") || ns.StartsWith("BepInEx")))
                        continue;

                    FieldInfo[] fields;
                    if (!fieldCache.TryGetValue(t, out fields))
                    {
                        FieldInfo[] all;
                        try { all = t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); }
                        catch { all = null; }
                        var keep = new List<FieldInfo>(8);
                        if (all != null)
                        {
                            for (int fi = 0; fi < all.Length; fi++)
                            {
                                var f = all[fi];
                                if ((object)f == null) continue;
                                if (f.FieldType.IsArray) continue; // skip arrays for speed
                                if (IsCollectionType(f.FieldType)) keep.Add(f);
                            }
                        }
                        fields = keep.ToArray();
                        fieldCache[t] = fields;
                    }

                    if (fields.Length == 0) continue;

                    for (int fi = 0; fi < fields.Length; fi++)
                    {
                        var f = fields[fi];
                        object v;
                        try { v = f.GetValue(c); } catch { continue; }
                        if (v == null) continue;
                        int count = TryGetCount(v);
                        if (count <= 0) continue;

                        var key = t.FullName + "." + f.Name;
                        int curT, curN;
                        totals.TryGetValue(key, out curT);
                        instanceCounts.TryGetValue(key, out curN);
                        totals[key] = curT + count;
                        instanceCounts[key] = curN + 1;
                    }
                }
            }
            catch (Exception e)
            {
                Logger.LogError("Instance collection scan failed: " + e);
                return;
            }

            var entries = new List<KeyValuePair<string, int>>(totals);
            entries.Sort((a, b) => {
                int c = b.Value.CompareTo(a.Value);
                return c != 0 ? c : string.CompareOrdinal(a.Key, b.Key);
            });

            int n = Math.Min(entries.Count, 60);
            var sb = new System.Text.StringBuilder();
            sb.Append($"[icol {tag}] instance collections (top {n}/{entries.Count}, format type.field=totalCount/instances): ");
            for (int i = 0; i < n; i++)
            {
                if (i > 0) sb.Append(", ");
                var key = entries[i].Key;
                int total = entries[i].Value;
                int inst;
                instanceCounts.TryGetValue(key, out inst);
                sb.Append(key).Append('=').Append(total).Append('/').Append(inst);
            }
            if (n == 0) sb.Append("(none)");
            Logger.LogMessage(sb.ToString());
        }

        private static List<string> ListDontDestroyOnLoadRoots()
        {
            var probe = new GameObject("__diag_ddol_probe__");
            DontDestroyOnLoad(probe);
            var ddolScene = probe.scene;
            var names = new List<string>();
            try
            {
                var roots = ddolScene.GetRootGameObjects();
                for (int i = 0; i < roots.Length; i++)
                {
                    if (roots[i] == probe) continue;
                    names.Add($"{roots[i].name}({roots[i].GetComponentsInChildren<Component>(true).Length}c)");
                }
            }
            finally
            {
                Destroy(probe);
            }
            return names;
        }

        private void RecomputeFrameStats()
        {
            int n = _frameFilled;
            if (n == 0) return;
            // Copy + sort for max/p99
            var snap = new float[n];
            Array.Copy(_frameMs, snap, n);
            Array.Sort(snap);
            _lastFrameMaxMs = snap[n - 1];
            int p99idx = (int)(n * 0.99f);
            if (p99idx >= n) p99idx = n - 1;
            _lastFrameP99Ms = snap[p99idx];
            int spikes = 0;
            for (int i = 0; i < n; i++) if (snap[i] > 33f) spikes++;
            _lastFrameSpikeCount = spikes;
        }

        // Reset cached callback refs to null without disposing — used if we hit a state issue.
        internal static void ClearSteamCallbackCache()
        {
            s_prevOverlayCallback = null;
            s_prevPlayerCountCallback = null;
        }

        private static int CountDontDestroyOnLoad()
        {
            var probe = new GameObject("__diag_ddol_probe__");
            DontDestroyOnLoad(probe);
            var ddolScene = probe.scene;
            int count;
            try
            {
                count = ddolScene.GetRootGameObjects().Length;
            }
            finally
            {
                Destroy(probe);
            }
            return count - 1;
        }
    }
}
