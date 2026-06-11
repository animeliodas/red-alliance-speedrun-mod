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
    [BepInPlugin(GUID, "Red Alliance Speedrun Mod", "1.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        public const string GUID = "redalliance.speedrun";

        internal static Plugin Instance;
        internal new static ManualLogSource Logger;
        internal static ConfigFile ConfigRef;

        // Statics mirrored from ConfigEntries (see SyncStatics) so RaMenu / racfg edits
        // apply without restart.
        internal static bool PracticeMode;
        internal static bool LiveSplitEnabled;
        internal static string LiveSplitHost;
        internal static int LiveSplitPort;
        internal static float LiveSplitSyncRate;
        internal static bool SkipPrison1Redirect;

        private ConfigEntry<KeyCode> _restartKey;
        private ConfigEntry<KeyCode> _diagKey;
        private ConfigEntry<KeyCode> _deepDiagKey;
        private ConfigEntry<KeyCode> _profilerDumpKey;
        private ConfigEntry<bool> _invokeRepeatingProfilerEnabled;
        private ConfigEntry<bool> _updateProfilerEnabled;
        private ConfigEntry<bool> _diagOnReload;
        private ConfigEntry<bool> _restartInMenu;
        private ConfigEntry<bool> _deltaOnReload;
        private ConfigEntry<bool> _orphanPrefabSweep;

        // --- Speedrun tools (ported from the v1.4 plugin; clip/movement mechanics and
        // SpeedrunMode deliberately NOT ported — different game build, not needed here) ---
        private ConfigEntry<KeyCode> _fpsToggleKey;
        private ConfigEntry<KeyCode> _menuToggleKey;
        private ConfigEntry<bool> _fpsLockEnabled;
        private ConfigEntry<int> _standartFPS;
        private ConfigEntry<int> _minFPS;
        private bool _fpsLow;
        private ConfigEntry<string> _restartLevel;
        private ConfigEntry<bool> _restartSetDifficulty;
        private ConfigEntry<int> _restartDifficultyValue;
        private ConfigEntry<bool> _skipPrison1Level;
        private ConfigEntry<bool> _deleteSavesOnRestart;
        private ConfigEntry<bool> _practiceMode;
        private ConfigEntry<bool> _livesplitEnabled;
        private ConfigEntry<string> _livesplitHost;
        private ConfigEntry<int> _livesplitPort;
        private ConfigEntry<float> _livesplitSyncRate;
        private ConfigEntry<string> _livesplitSplitScenes;
        private ConfigEntry<bool> _livesplitSplitOnLevelChange;
        private ConfigEntry<bool> _showTimers;
        private ConfigEntry<bool> _skipPrison1;
        private ConfigEntry<int> _timerScreenX;
        private ConfigEntry<int> _timerScreenY;
        private ConfigEntry<int> _timerFontSize;
        private ConfigEntry<string> _runEndScene;

        // Timer state
        private readonly SpeedrunTimer _rta = new SpeedrunTimer("RTA", false, false);
        private readonly SpeedrunTimer _igt = new SpeedrunTimer("IGT", true, true);
        private bool _waitingForLoadComplete; // after TAB, start timers when load completes
        private bool _runIsActive;            // true between run start and RunEndScene load
        private string _pendingStartScene;    // timer starts when this scene finishes loading
        private int _lastSplitBuildIndex = -1; // splits only fire for scenes with a HIGHER build index
        private float _livesplitNextSyncTime;
        private string[] _livesplitSplitSceneList;
        private string _livesplitLastSplitScene;

        // GUI cache
        private GUIStyle _timerStyle;
        private GUIStyle _timerShadowStyle;

        // Previous-restart census snapshots, for the auto-delta leak logger. Each restart we
        // census component-type counts and DDOL-root component counts, then log only what GREW
        // since the previous restart. A type that climbs by a constant amount every restart is
        // a leak (object created without the matching destroy). Avoids manual F12 diffing.
        private Dictionary<string, int> _prevCompCounts;
        private Dictionary<string, int> _prevDdolCounts;
        private int _prevTotalGO = -1;
        private int _prevTotalComp = -1;
        private int _deltaSampleCount;
        // Baseline (first census) totals: cumulative drift vs these is the true leak signal;
        // per-restart deltas oscillate with load/unload timing.
        private int _baseTotalGO = -1;
        private int _baseTotalComp = -1;

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

        // Allocation-rate sample (KB/sec). Positive per-frame deltas of GC.GetTotalMemory are
        // accumulated; sampling once per second misses everything a GC already collected inside
        // the window, which underreports badly when GCs run several times per second.
        private float _allocLastSampleTime;
        private long _allocPrevFrameBytes;
        private long _allocAccumBytes;
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
            ConfigRef = Config;

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
                "Diagnostics", "InvokeRepeatingProfiler", false,
                "Time every call to common ~1-second InvokeRepeating callbacks (GlobalAIScript, LightDistanceCullingScript, ObjectDisableScript, FootStepsScriptNew, AILightHeightOptimizationScript). F11 dumps top consumers.");
            _updateProfilerEnabled = Config.Bind(
                "Diagnostics", "UpdateProfiler", false,
                "Patch Update/LateUpdate/FixedUpdate of EVERY game MonoBehaviour subclass with a Stopwatch timer. Heavy (patches 50-100+ methods) but reveals which per-frame method actually takes time. F11 dumps top consumers.");
            _diagOnReload = Config.Bind(
                "Diagnostics", "LogOnLevelLoad", false,
                "Log RT/material/audio/DDOL counts after every scene load.");
            _deltaOnReload = Config.Bind(
                "Diagnostics", "DeltaOnLevelLoad", false,
                "After each gameplay scene load, census component-type counts + DDOL-root component " +
                "counts and log ONLY what GREW since the previous restart ([delta] lines). A type that " +
                "climbs by a constant amount every restart is a leak. Removes need for manual F12 diffing. " +
                "Adds one full-object census per load (~deep-diag cost); set false to disable.");
            _orphanPrefabSweep = Config.Bind(
                "LeakFix", "OrphanPrefabSweep", false,
                "Each Transfer-mode restart strands a full copy of the player and _Canvas_Player " +
                "prefab templates in asset space (outside any scene), where scene reloads never " +
                "destroy them and UnloadUnusedAssets cannot free them (lingering managed refs). " +
                "~1450 GameObjects leak per restart, inflating every GC pass and FindObjectsOfType " +
                "scan into 200-400ms spikes. This sweep destroys duplicate asset-space templates " +
                "after each load, keeping only those referenced by the live NetworkManager and " +
                "PlayerStatsSp.");
            _restartInMenu = Config.Bind(
                "Hotkeys", "RestartInMenu", false,
                "If false, the restart key is ignored when the active scene is main_menu, credits, or start_screen.");
            _spikeLogThresholdMs = Config.Bind(
                "Diagnostics", "SpikeLogThresholdMs", 50f,
                "Log every individual frame whose duration exceeds this many ms, with timing " +
                "relative to the last scene load. Set to 0 to disable.");

            SceneManager.sceneLoaded += OnSceneLoaded;
            Application.logMessageReceived += OnLogCount;

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

            // All freeze fixes and CPU optimizations live in the separate
            // "Red Alliance v1.3 Optimization Fix" plugin (redalliance.optimizationfix)
            // since v0.20.0, so both plugins can be installed side by side without
            // double-patching. Warn if it's missing — without it the game's own bugs
            // (immortal GC threads, per-frame LUT rebuild) make it stutter after
            // 20-30 level loads, which ruins long speedrun sessions.
            try
            {
                if (!BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("redalliance.optimizationfix"))
                {
                    Logger.LogWarning(
                        "[deps] 'Red Alliance v1.3 Optimization Fix' (RedAllianceOptimizationFix.dll) is NOT " +
                        "installed. The game will progressively freeze after ~20-30 level loads due to its " +
                        "own bugs. Strongly recommended for speedrun sessions.");
                }
            }
            catch { /* chainloader not ready — ignore */ }

            // --- Speedrun tools (ported from v1.4 plugin) ---
            _fpsToggleKey = Config.Bind(
                "Hotkeys", "ToggleFPSKey", KeyCode.Q,
                "Hotkey to swap Application.targetFrameRate between StandartFPS and MinFPS. " +
                "Low FPS (long Time.deltaTime) is used for certain frame-timing tricks.");
            _menuToggleKey = Config.Bind(
                "Hotkeys", "ToggleMenuKey", KeyCode.F10,
                "Toggle the in-game speedrun menu (level launcher, config editor).");

            _fpsLockEnabled = Config.Bind(
                "FPS", "FPSLockEnabled", true,
                "Enforce Application.targetFrameRate (StandartFPS / MinFPS via toggle key) every " +
                "frame, with vsync forced off. Set false to leave frame pacing entirely to the game.");
            _standartFPS = Config.Bind(
                "FPS", "StandartFPS", 100,
                "Target FPS for normal gameplay. Set to -1 to uncap.");
            _minFPS = Config.Bind(
                "FPS", "MinFPS", 5,
                "Target FPS when toggled low (Q by default). Intentionally low to produce big " +
                "frame deltas — useful for tricks that need a lag-spike-like timing.");

            _restartLevel = Config.Bind(
                "Restart", "RestartLevel", "",
                "Scene name to load when RestartKey is pressed. Leave EMPTY to reload the " +
                "currently active scene. Example: 'mountains_1', 'prison_1', 'prologue_1'.");
            _restartSetDifficulty = Config.Bind(
                "Restart", "RestartSetDifficulty", true,
                "On TAB restart, force NetworkManager.gameDifficulty to RestartDifficultyValue. " +
                "Game auto-resets to Normal (1) on relog; this overrides back to your run value.");
            _restartDifficultyValue = Config.Bind(
                "Restart", "RestartDifficultyValue", 0,
                "Difficulty level set on restart. 0=Easy, 1=Normal, 2=Hard, 3=Hardcore.");
            _skipPrison1Level = Config.Bind(
                "Restart", "SkipPrison1Level", true,
                "Redirect every load of 'prison_1' (a ~38s cutscene level) to 'prison_2'. " +
                "Keeps run timing simple: the speedrun route loads prison_2 directly. " +
                "Pair with Timers.SkipPrison1 if your category counts the cutscene time.");
            SkipPrison1Redirect = _skipPrison1Level.Value;
            _deleteSavesOnRestart = Config.Bind(
                "Restart", "DeleteSavesOnRestart", true,
                "Delete all save slots (Assets/SaveData/red-alliance-*.cfg) on every TAB " +
                "restart / menu level launch. Prevents loading a save to jump levels ahead " +
                "mid-run, which would corrupt splits and run integrity. gameData.cfg " +
                "(settings/achievements) is never touched. WARNING: wipes normal-play saves " +
                "too — set false if you alternate speedruns with a casual playthrough.");
            _practiceMode = Config.Bind(
                "Restart", "PracticeMode", false,
                "When true, PlayerStatsSp.CheatsEnabled (desu command) is allowed to stay " +
                "on for training. When false (default), CheatsEnabled is forced to false " +
                "on TAB restart and every SceneLoaded — runs guaranteed cheat-free. The " +
                "cheats_enabled flag is recorded in the end-of-run dump regardless.");
            PracticeMode = _practiceMode.Value;

            _livesplitEnabled = Config.Bind(
                "LiveSplit", "Enabled", false,
                "Send timer commands to LiveSplit via its Server component (TCP). " +
                "Install LiveSplit.Server.dll in LiveSplit, add the 'LiveSplit Server' " +
                "component to your layout, right-click → Start Server before launching.");
            LiveSplitEnabled = _livesplitEnabled.Value;
            _livesplitHost = Config.Bind(
                "LiveSplit", "Host", "127.0.0.1",
                "LiveSplit Server host (default localhost).");
            LiveSplitHost = _livesplitHost.Value;
            _livesplitPort = Config.Bind(
                "LiveSplit", "Port", 16834,
                "LiveSplit Server TCP port (default 16834).");
            LiveSplitPort = _livesplitPort.Value;
            _livesplitSyncRate = Config.Bind(
                "LiveSplit", "SyncRateHz", 10f,
                "How many times per second we push IGT via setgametime. 10 = every 100ms.");
            LiveSplitSyncRate = _livesplitSyncRate.Value;
            _livesplitSplitOnLevelChange = Config.Bind(
                "LiveSplit", "SplitOnLevelChange", true,
                "Split on every scene load while a run is active. Skips the start scene " +
                "(no split before timer starts) and RunEndScene (handled by the end-of-run " +
                "block). Overrides AutoSplitScenes when on.");
            _livesplitSplitScenes = Config.Bind(
                "LiveSplit", "AutoSplitScenes", "",
                "Comma-separated scene names that trigger split() on load. Used only when " +
                "SplitOnLevelChange = false. Example: prison_2,mountains_1. Each scene " +
                "splits at most once per run.");
            UpdateLivesplitSplitScenes();

            _showTimers = Config.Bind(
                "Timers", "ShowTimers", true,
                "Draw the RTA and IGT timers on screen.");
            _skipPrison1 = Config.Bind(
                "Timers", "SkipPrison1", false,
                "If true, both timers start at 38.470 instead of 0 (compensates for the " +
                "prison_1 skip).");
            _timerScreenX = Config.Bind(
                "Timers", "ScreenX", 16,
                "Pixels from the left edge of the screen.");
            _timerScreenY = Config.Bind(
                "Timers", "ScreenY", 16,
                "Pixels from the top edge of the screen.");
            _timerFontSize = Config.Bind(
                "Timers", "FontSize", 22,
                "Font size of the timer labels.");
            _runEndScene = Config.Bind(
                "Timers", "RunEndScene", "credits",
                "Scene name that ends the run. Both timers stop when this scene loads.");

            try
            {
                var harmony = new Harmony(GUID + ".racfg");
                harmony.PatchAll(typeof(ConsoleConfigCommandPatch));
                Logger.LogInfo("[racfg] Console command installed (type 'racfg help' in dev console).");
            }
            catch (Exception e)
            {
                Logger.LogError("[racfg] Failed to patch: " + e);
            }

            try
            {
                var harmony = new Harmony(GUID + ".sceneflow");
                harmony.PatchAll(typeof(LoadLevelDuplicateGuardPatch));
                harmony.PatchAll(typeof(SkipPrison1Patch));
                harmony.PatchAll(typeof(SkipPrison1RpcPatch));
                harmony.PatchAll(typeof(SkipPrison1AsyncPatch));
                harmony.PatchAll(typeof(SaveLoadDetectPatch));
                Logger.LogInfo("[sceneflow] Patched: duplicate-load guard (black screen fix), prison_1 skip, save-load split detector.");
            }
            catch (Exception e)
            {
                Logger.LogError("[sceneflow] Failed to patch: " + e);
            }

            ApplyFpsCap();

            Logger.LogInfo($"Restart key: {_restartKey.Value}; Diag key: {_diagKey.Value}; Menu key: {_menuToggleKey.Value}");
        }

        // Copies ConfigEntry.Value into the internal static fields. Called by Awake (after
        // all Config.Bind calls) and by RaMenu / the racfg console command after a runtime
        // edit so code that reads the statics sees new values without a restart.
        internal void SyncStatics()
        {
            PracticeMode = _practiceMode.Value;
            SkipPrison1Redirect = _skipPrison1Level.Value;
            LiveSplitEnabled = _livesplitEnabled.Value;
            LiveSplitHost = _livesplitHost.Value;
            LiveSplitPort = _livesplitPort.Value;
            LiveSplitSyncRate = _livesplitSyncRate.Value;
            UpdateLivesplitSplitScenes();
        }

        private void UpdateLivesplitSplitScenes()
        {
            string raw = _livesplitSplitScenes?.Value ?? "";
            if (string.IsNullOrEmpty(raw)) { _livesplitSplitSceneList = new string[0]; return; }
            var parts = raw.Split(',');
            for (int i = 0; i < parts.Length; i++) parts[i] = parts[i].Trim();
            _livesplitSplitSceneList = parts;
        }

        // Forces PlayerStatsSp.CheatsEnabled = false unless PracticeMode is on. Called
        // from TryQuickRestart (run start) and OnSceneLoaded (every scene transition) so
        // cheats can't sneak back via game's own load/state-restore paths.
        private void ApplyCheatGate()
        {
            if (_practiceMode == null || _practiceMode.Value) return;
            try
            {
                if (PlayerStatsSp.CheatsEnabled)
                {
                    PlayerStatsSp.CheatsEnabled = false;
                    Logger.LogInfo("[cheatgate] CheatsEnabled forced to false (PracticeMode=off)");
                }
            }
            catch (Exception e) { Logger.LogWarning("CheatGate failed: " + e.Message); }
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            Application.logMessageReceived -= OnLogCount;
            LiveSplitClient.Disconnect();
        }

        private void OnLogCount(string _, string __, LogType ___)
        {
            // Just count — don't allocate. Cheap counter to detect log-spam growth.
            _logCountThisWindow++;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (mode != LoadSceneMode.Single) return;

            // A load finished — re-arm the duplicate-load guard for the next one.
            LoadLevelDuplicateGuardPatch.Clear();

            // Reset spike-log timing baseline. Increment restart counter for any non-startup scene.
            _lastSceneLoadTime = Time.unscaledTime;
            _frameInWindow = 0;
            if (scene.name != "start_screen" && scene.name != "object_pool" &&
                scene.name != "main_menu" && scene.name != "credits")
            {
                _restartCount++;
            }

            if (_diagOnReload != null && _diagOnReload.Value)
            {
                LogDiagnostics("post-load:" + scene.name);
            }

            // Don't touch early-startup scenes: anything we sweep here can take the loading
            // screen / main menu transition down before the player ever sees gameplay.
            bool earlyScene =
                scene.name == "start_screen" ||
                scene.name == "object_pool" ||
                scene.name == "main_menu" ||
                scene.name == "credits";
            if (!earlyScene &&
                ((_deltaOnReload != null && _deltaOnReload.Value) ||
                 (_orphanPrefabSweep != null && _orphanPrefabSweep.Value)))
            {
                StartCoroutine(DeltaAfterLoad(scene.name));
            }

            ApplyCheatGate();

            // Timer start: only when our designated start scene actually finishes loading
            // (after click-to-continue). Triggered exactly once per restart. _runIsActive
            // becomes true here, so the auto-split block below sees it during scene
            // transitions, but _livesplitLastSplitScene = scene.name prevents splitting
            // the start scene itself.
            if (_waitingForLoadComplete && !string.IsNullOrEmpty(_pendingStartScene)
                && scene.name == _pendingStartScene)
            {
                double initial = _skipPrison1.Value ? 38.470 : 0.0;
                _rta.Reset(initial);
                _igt.Reset(initial);
                _rta.Start();
                _igt.Start();
                _runIsActive = true;
                _waitingForLoadComplete = false;
                _livesplitLastSplitScene = scene.name; // suppress split on the start scene
                _lastSplitBuildIndex = scene.buildIndex; // splits only fire for higher level IDs
                _pendingStartScene = null;
                Logger.LogInfo($"[timer] run started on scene '{scene.name}' (initial offset {initial:F3}s)");
                LiveSplitClient.StartTimer();
                _livesplitNextSyncTime = 0f;
            }

            // Auto-split logic. SplitOnLevelChange fires once per distinct scene change
            // during an active run, skipping the run-end scene (handled below). When that
            // flag is off, fall back to the AutoSplitScenes whitelist.
            if (_runIsActive && scene.name != _runEndScene.Value)
            {
                bool shouldSplit = false;
                if (_livesplitSplitOnLevelChange != null && _livesplitSplitOnLevelChange.Value)
                {
                    if (_livesplitLastSplitScene != scene.name) shouldSplit = true;
                }
                else if (_livesplitSplitSceneList != null && _livesplitSplitSceneList.Length > 0)
                {
                    foreach (var trigger in _livesplitSplitSceneList)
                    {
                        if (string.IsNullOrEmpty(trigger)) continue;
                        if (scene.name == trigger && _livesplitLastSplitScene != trigger)
                        {
                            shouldSplit = true;
                            break;
                        }
                    }
                }

                // Level-ID guard: split only when the level ID (scene build index — the game
                // orders levels by progression) strictly INCREASES. Replays, backtracks and
                // restarts of already-split levels never split twice.
                if (shouldSplit && scene.buildIndex <= _lastSplitBuildIndex)
                {
                    shouldSplit = false;
                    Logger.LogInfo($"[splitguard] no split: '{scene.name}' id={scene.buildIndex} <= last split id={_lastSplitBuildIndex}");
                }

                // Save-load guard: a scene change caused by loading a save is not run
                // progress — no split, even if it's the next level. The level-ID watermark
                // still advances so the jumped-over levels can't split later either.
                bool saveLoad = SaveLoadDetectPatch.IsActive();
                try { saveLoad |= GameSaveScript.Loading; } catch { }
                if (shouldSplit && saveLoad)
                {
                    shouldSplit = false;
                    _livesplitLastSplitScene = scene.name;
                    if (scene.buildIndex > _lastSplitBuildIndex) _lastSplitBuildIndex = scene.buildIndex;
                    Logger.LogInfo($"[splitguard] no split: '{scene.name}' was reached by loading a save.");
                }

                if (shouldSplit)
                {
                    _livesplitLastSplitScene = scene.name;
                    _lastSplitBuildIndex = scene.buildIndex;
                    LiveSplitClient.Split();
                    Logger.LogInfo($"[livesplit] split on scene '{scene.name}' (id={scene.buildIndex})");
                }
            }
            SaveLoadDetectPatch.Clear();

            // End-of-run: stop both timers when the run-end scene (credits) loads.
            if (scene.name == _runEndScene.Value && _runIsActive)
            {
                _rta.Stop();
                _igt.Stop();
                _runIsActive = false;
                Logger.LogInfo($"[timer] run ended on scene '{scene.name}'. RTA={_rta.Format()} IGT={_igt.Format()}");
                LiveSplitClient.SetGameTime(_igt.Elapsed); // final IGT push
                LiveSplitClient.Split();
                DumpRunConfigForModeration();
            }
        }

        // End-of-run audit dump. Lists every ConfigEntry value plus a content hash for
        // moderators to verify a speedrun submission against. Spans multiple log lines
        // bracketed by markers so it's easy to extract from BepInEx/LogOutput.log.
        private void DumpRunConfigForModeration()
        {
            try
            {
                var lines = new List<string>();
                if (ConfigRef != null)
                {
                    foreach (var def in ConfigRef.Keys)
                    {
                        var entry = ConfigRef[def];
                        lines.Add($"{def.Section}.{def.Key}={entry.BoxedValue}");
                    }
                }
                lines.Sort(StringComparer.Ordinal);

                ulong hash = 14695981039346656037UL;
                foreach (var l in lines)
                {
                    foreach (var ch in l) { hash ^= ch; hash *= 1099511628211UL; }
                    hash ^= (byte)'\n'; hash *= 1099511628211UL;
                }
                string hashStr = $"fnv1a64:{hash:X16}";

                bool cheats = false;
                try { cheats = PlayerStatsSp.CheatsEnabled; } catch { }

                string[] header = new[]
                {
                    "===== [RACFG-DUMP-BEGIN] =====",
                    $"plugin_version=1.0.0",
                    $"game_version=1.3",
                    $"rta={_rta.Format()}",
                    $"igt={_igt.Format()}",
                    $"end_scene={SceneManager.GetActiveScene().name}",
                    $"timestamp_utc={DateTime.UtcNow:o}",
                    $"cheats_enabled={cheats}",
                    $"config_hash={hashStr}",
                    $"config_entries={lines.Count}",
                };

                foreach (var h in header) Emit(h);
                foreach (var l in lines) Emit("  " + l);
                Emit("===== [RACFG-DUMP-END] =====");
            }
            catch (Exception e)
            {
                Logger.LogError("[racfg] dump failed: " + e);
            }
        }

        // Emit a line both to BepInEx log and the in-game developer console (if available).
        private static void Emit(string line)
        {
            Logger.LogMessage(line);
            try { DeveloperConsoleScript.AddConsoleMessage(line); }
            catch { /* console may not be initialized */ }
        }

        // Wait for the scene to fully settle (past the load-spike burst), then sweep orphan
        // prefab templates and census per-restart growth. Sweep runs first so the census
        // measures the post-fix state. Delayed enough that the new NetworkManager spawned the
        // player and PlayerStatsSp.Awake instantiated the canvas (their templates must be live
        // so the sweep knows what to keep).
        private IEnumerator DeltaAfterLoad(string sceneName)
        {
            for (int i = 0; i < 25; i++) yield return null;
            if (_orphanPrefabSweep != null && _orphanPrefabSweep.Value)
            {
                SweepOrphanPrefabTemplates(sceneName);
            }
            for (int i = 0; i < 5; i++) yield return null;
            if (_deltaOnReload != null && _deltaOnReload.Value)
            {
                LogRestartDelta(sceneName);
            }
        }

        // Destroy duplicate prefab-template copies stranded in asset space by Transfer restarts.
        // Only touches root objects outside any scene, with default hideFlags, whose name matches
        // a known leaked template (player prefab, _Canvas_Player), and which are NOT the copy
        // currently referenced by the live NetworkManager / PlayerStatsSp.
        private void SweepOrphanPrefabTemplates(string tag)
        {
            try
            {
                var keepIds = new HashSet<int>();
                var sweepNames = new HashSet<string> { "_Canvas_Player" };

                var nm = NetworkManager.Instance;
                if ((bool)nm)
                {
                    var f = typeof(NetworkManager).GetField("playerPrefab",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    var prefab = ((object)f != null) ? f.GetValue(nm) as GameObject : null;
                    if ((bool)prefab)
                    {
                        keepIds.Add(prefab.GetInstanceID());
                        sweepNames.Add(prefab.name);
                    }
                }
                var wc = WorldComponents.Instance;
                if ((bool)wc)
                {
                    var pss = wc.PlayerStatsSp;
                    if ((bool)pss && (bool)pss.hudCanvas)
                    {
                        keepIds.Add(pss.hudCanvas.GetInstanceID());
                    }
                }
                if (keepIds.Count == 0)
                {
                    // No live template references found — destroying anything now could kill the
                    // only remaining copy. Skip this load.
                    Logger.LogWarning($"[orphanfix {tag}] no live template refs; sweep skipped.");
                    return;
                }

                int destroyed = 0, comps = 0;
                var all = Resources.FindObjectsOfTypeAll<GameObject>();
                for (int i = 0; i < all.Length; i++)
                {
                    var go = all[i];
                    if (go == null) continue;
                    if (go.scene.IsValid()) continue;                 // asset space only
                    if (go.transform.parent != null) continue;        // roots only
                    // Match the template name itself or a stranded runtime clone of it
                    // ("_Canvas_Player(Clone)"); clones in asset space are always leaks.
                    string baseName = go.name.EndsWith("(Clone)")
                        ? go.name.Substring(0, go.name.Length - 7)
                        : go.name;
                    if (!sweepNames.Contains(baseName)) continue;
                    if (keepIds.Contains(go.GetInstanceID())) continue;
                    comps += go.GetComponentsInChildren<Component>(true).Length;
                    DestroyImmediate(go, true);
                    destroyed++;
                }
                if (destroyed > 0)
                {
                    Resources.UnloadUnusedAssets();
                    GC.Collect();
                }
                Logger.LogMessage(
                    $"[orphanfix {tag}] destroyed={destroyed} orphan template root(s) (~{comps} components)  " +
                    $"sweepNames=[{string.Join(", ", new List<string>(sweepNames).ToArray())}]");
            }
            catch (Exception e)
            {
                Logger.LogError("Orphan prefab sweep failed: " + e);
            }
        }

        // Census component-type counts + DDOL-root component counts, then log only entries that
        // grew vs the previous restart. Constant per-restart growth == a leak. Heavy (one full
        // FindObjectsOfTypeAll<Component> walk) but runs once per load.
        private void LogRestartDelta(string tag)
        {
            try
            {
                // Component-type census.
                var compCounts = new Dictionary<string, int>(1024);
                var allComps = Resources.FindObjectsOfTypeAll<Component>();
                int totalComp = 0;
                for (int i = 0; i < allComps.Length; i++)
                {
                    var c = allComps[i];
                    if (c == null) continue;
                    totalComp++;
                    var n = c.GetType().Name;
                    int v;
                    compCounts.TryGetValue(n, out v);
                    compCounts[n] = v + 1;
                }
                int totalGO = Resources.FindObjectsOfTypeAll<GameObject>().Length;

                // DDOL-root component-count census (Object_Pool, _NetworkManager, etc.).
                var ddolCounts = DontDestroyOnLoadRootCounts();

                if (_prevCompCounts == null)
                {
                    _baseTotalGO = totalGO;
                    _baseTotalComp = totalComp;
                    Logger.LogMessage(
                        $"[delta {tag}] baseline #{_deltaSampleCount}: GO={totalGO} Comp={totalComp} " +
                        $"ddolRoots={ddolCounts.Count}. Restart again to see growth.");
                }
                else
                {
                    int dGO = totalGO - _prevTotalGO;
                    int dComp = totalComp - _prevTotalComp;
                    // Cumulative drift vs baseline + perf trend: the one line that matters for
                    // "does it degrade per restart". Flat cum + rising gc/s = allocation problem,
                    // not an object leak.
                    Logger.LogMessage(
                        $"[delta-cum {tag} #{_deltaSampleCount}] vs baseline: dGO={totalGO - _baseTotalGO:+#;-#;0} " +
                        $"dComp={totalComp - _baseTotalComp:+#;-#;0}  perf: fps={_lastFps:F1} gc/s={_lastGcPerSec:F1} " +
                        $"alloc={_lastAllocKbPerSec:F0}KB/s p99={_lastFrameP99Ms:F1}ms");

                    // Grown component types, by descending delta.
                    var grown = new List<KeyValuePair<string, int>>();
                    foreach (var kv in compCounts)
                    {
                        int prev;
                        _prevCompCounts.TryGetValue(kv.Key, out prev);
                        int d = kv.Value - prev;
                        if (d > 0) grown.Add(new KeyValuePair<string, int>(kv.Key, d));
                    }
                    grown.Sort((a, b) => b.Value.CompareTo(a.Value));

                    var sb = new System.Text.StringBuilder();
                    sb.Append($"[delta {tag} #{_deltaSampleCount}] dGO={dGO:+#;-#;0} dComp={dComp:+#;-#;0}  grownTypes: ");
                    if (grown.Count == 0) sb.Append("(none)");
                    int top = Math.Min(15, grown.Count);
                    for (int i = 0; i < top; i++)
                    {
                        if (i > 0) sb.Append(", ");
                        sb.Append(grown[i].Key).Append("+").Append(grown[i].Value);
                    }
                    Logger.LogMessage(sb.ToString());

                    // Locate the top grown types: histogram of where their instances live
                    // (scene/rootObject, or asset/hidden if not in a loaded scene). Distinguishes
                    // a scene-object leak from assets pinned in memory.
                    int locTypes = Math.Min(5, grown.Count);
                    for (int t = 0; t < locTypes; t++)
                    {
                        string typeName = grown[t].Key;
                        var locCounts = new Dictionary<string, int>(16);
                        for (int i = 0; i < allComps.Length; i++)
                        {
                            var c = allComps[i];
                            if (c == null || c.GetType().Name != typeName) continue;
                            string loc;
                            var go = c.gameObject;
                            var s = go.scene;
                            var tr = go.transform;
                            while (tr.parent != null) tr = tr.parent;
                            if (s.IsValid())
                            {
                                loc = s.name + "/" + tr.name + (go.activeInHierarchy ? "" : "(off)");
                            }
                            else
                            {
                                loc = "asset/" + tr.name + "(hf=" + tr.gameObject.hideFlags + ")";
                            }
                            int v;
                            locCounts.TryGetValue(loc, out v);
                            locCounts[loc] = v + 1;
                        }
                        var locs = new List<KeyValuePair<string, int>>(locCounts);
                        locs.Sort((a, b) => b.Value.CompareTo(a.Value));
                        var lb = new System.Text.StringBuilder();
                        lb.Append($"[delta-loc {tag} #{_deltaSampleCount}] {typeName}: ");
                        int topL = Math.Min(6, locs.Count);
                        for (int i = 0; i < topL; i++)
                        {
                            if (i > 0) lb.Append(", ");
                            lb.Append(locs[i].Key).Append("=").Append(locs[i].Value);
                        }
                        Logger.LogMessage(lb.ToString());
                    }

                    // Asset-space root census: every root GameObject outside any scene, with its
                    // hideFlags and hierarchy component count. Leaked template copies show up here
                    // as repeated names. Logged every delta so growth across restarts is visible.
                    var assetRootComps = new Dictionary<string, int>(64);
                    var assetRootInstances = new Dictionary<string, int>(64);
                    var allGOs2 = Resources.FindObjectsOfTypeAll<GameObject>();
                    for (int i = 0; i < allGOs2.Length; i++)
                    {
                        var go = allGOs2[i];
                        if (go == null || go.scene.IsValid() || go.transform.parent != null) continue;
                        int cc = go.GetComponentsInChildren<Component>(true).Length;
                        if (cc < 25) continue; // skip tiny utility prefabs; leak copies are huge
                        string key = go.name + "(hf=" + go.hideFlags + ")";
                        int v;
                        assetRootComps.TryGetValue(key, out v);
                        assetRootComps[key] = v + cc;
                        assetRootInstances.TryGetValue(key, out v);
                        assetRootInstances[key] = v + 1;
                    }
                    var rootsSorted = new List<KeyValuePair<string, int>>(assetRootComps);
                    rootsSorted.Sort((a, b) => b.Value.CompareTo(a.Value));
                    var ab = new System.Text.StringBuilder();
                    ab.Append($"[delta-asset {tag} #{_deltaSampleCount}] big asset roots (name=copies/totalComps): ");
                    int topA = Math.Min(12, rootsSorted.Count);
                    for (int i = 0; i < topA; i++)
                    {
                        if (i > 0) ab.Append(", ");
                        ab.Append(rootsSorted[i].Key).Append("=")
                          .Append(assetRootInstances[rootsSorted[i].Key]).Append("/")
                          .Append(rootsSorted[i].Value);
                    }
                    Logger.LogMessage(ab.ToString());

                    // Grown DDOL roots.
                    var ddolGrown = new List<string>();
                    foreach (var kv in ddolCounts)
                    {
                        int prev;
                        _prevDdolCounts.TryGetValue(kv.Key, out prev);
                        int d = kv.Value - prev;
                        if (d != 0) ddolGrown.Add($"{kv.Key}{d:+#;-#;0}(now {kv.Value})");
                    }
                    Logger.LogMessage("[delta " + tag + " #" + _deltaSampleCount + "] ddolRootDelta: " +
                        (ddolGrown.Count == 0 ? "(none)" : string.Join(", ", ddolGrown.ToArray())));
                }

                _prevCompCounts = compCounts;
                _prevDdolCounts = ddolCounts;
                _prevTotalGO = totalGO;
                _prevTotalComp = totalComp;
                _deltaSampleCount++;
            }
            catch (Exception e)
            {
                Logger.LogError("Delta census failed: " + e);
            }
        }

        // DDOL root name -> total component count in its hierarchy. Duplicate root names are summed.
        private static Dictionary<string, int> DontDestroyOnLoadRootCounts()
        {
            var probe = new GameObject("__diag_ddol_probe__");
            DontDestroyOnLoad(probe);
            var result = new Dictionary<string, int>(32);
            try
            {
                var roots = probe.scene.GetRootGameObjects();
                for (int i = 0; i < roots.Length; i++)
                {
                    if (roots[i] == probe) continue;
                    int c = roots[i].GetComponentsInChildren<Component>(true).Length;
                    int v;
                    result.TryGetValue(roots[i].name, out v);
                    result[roots[i].name] = v + c;
                }
            }
            finally
            {
                Destroy(probe);
            }
            return result;
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

            // Accumulate this frame's managed allocation (positive heap delta only; a GC inside
            // the frame makes the delta negative, which we ignore — we measure allocation, not
            // survival).
            long heapNow = GC.GetTotalMemory(false);
            if (heapNow > _allocPrevFrameBytes)
            {
                _allocAccumBytes += heapNow - _allocPrevFrameBytes;
            }
            _allocPrevFrameBytes = heapNow;

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
                int gcNow = GC.CollectionCount(0);

                if (_allocLastSampleTime > 0f)
                {
                    float dt = now - _allocLastSampleTime;
                    _lastAllocKbPerSec = (_allocAccumBytes / 1024f) / dt;
                    _lastGcPerSec = (gcNow - _lastGcCount) / dt;
                    _lastLogsPerSec = (int)(_logCountThisWindow / dt);
                }

                _allocAccumBytes = 0;
                _allocLastSampleTime = now;
                _lastGcCount = gcNow;
                _logCountThisWindow = 0;
            }

            // Periodic IGT push to LiveSplit Server.
            if (LiveSplitEnabled && _runIsActive && LiveSplitSyncRate > 0f)
            {
                if (Time.unscaledTime >= _livesplitNextSyncTime)
                {
                    LiveSplitClient.SetGameTime(_igt.Elapsed);
                    _livesplitNextSyncTime = Time.unscaledTime + (1f / LiveSplitSyncRate);
                }
            }

            // Tick both timers with this frame's real elapsed time.
            {
                double dt = Time.unscaledDeltaTime;
                bool isLoading = SceneManagerScript.LoadingLevel;
                bool isPaused = false;
                try { isPaused = PauseMenuScript.paused; } catch { }
                _rta.Tick(dt, isLoading, isPaused);
                _igt.Tick(dt, isLoading, isPaused);
            }

            if (Input.GetKeyDown(_restartKey.Value))
            {
                TryQuickRestart();
            }
            if (Input.GetKeyDown(_fpsToggleKey.Value))
            {
                ToggleFpsCap();
            }
            if (Input.GetKeyDown(_menuToggleKey.Value))
            {
                RaMenu.Toggle();
            }

            // Enforce target FPS every frame — game settings menus, vsync changes, etc. may
            // overwrite it otherwise. Cheap to set.
            ApplyFpsCap();
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

            // Target scene: configured RestartLevel if non-empty, else current scene.
            string target = (_restartLevel.Value != null && _restartLevel.Value.Length > 0)
                ? _restartLevel.Value
                : active;

            var loadType = (target == "main_menu" || target == "credits")
                ? LevelLoadingType.Reset
                : LevelLoadingType.Transfer;

            Logger.LogInfo($"Quick restart -> '{target}' (from '{active}', loadType={loadType}).");
            try { NetworkManager.OnStartedNewGame(); }
            catch (Exception e) { Logger.LogWarning("OnStartedNewGame threw: " + e.Message); }

            if (_restartSetDifficulty.Value)
            {
                try
                {
                    int diffInt = Mathf.Clamp(_restartDifficultyValue.Value, 0, 3);
                    NetworkManager.gameDifficulty = (GameDifficulty)diffInt;
                    var wc = WorldComponents.Instance;
                    if ((bool)wc && (object)wc.GameSettingsScript != null)
                        wc.GameSettingsScript.UpdateDifficultyLabel();
                    Logger.LogInfo($"[restart] gameDifficulty = {(GameDifficulty)diffInt} ({diffInt})");
                }
                catch (Exception e) { Logger.LogWarning("Set difficulty failed: " + e.Message); }
            }

            ApplyCheatGate();
            PrepareTimersForLaunch(target);

            smgr.StartCoroutine(smgr.LoadLevel(target, 0f, false, Vector3.zero, Vector3.zero, loadType));
        }

        // Reset and prime the timers — they start in OnSceneLoaded once the target scene
        // finishes loading. Also called by RaMenu before launching a level so menu launches
        // behave exactly like a TAB restart.
        internal void PrepareTimersForLaunch(string sceneName)
        {
            double initial = _skipPrison1.Value ? 38.470 : 0.0;
            _rta.Reset(initial);
            _igt.Reset(initial);
            _waitingForLoadComplete = true;
            _runIsActive = false;
            _livesplitLastSplitScene = null;
            _lastSplitBuildIndex = -1;
            _pendingStartScene = sceneName;
            LiveSplitClient.Reset();
            if (_deleteSavesOnRestart != null && _deleteSavesOnRestart.Value)
            {
                DeleteSaveSlots();
            }
        }

        // Deletes the 10 save-slot files so a mid-run save load (level jump) is impossible.
        // Only red-alliance-*.cfg slots — gameData.cfg (settings, achievements, counters)
        // lives in Assets/Resources and is never touched.
        private void DeleteSaveSlots()
        {
            try
            {
                string dir = System.IO.Path.GetFullPath("Assets\\SaveData");
                if (!System.IO.Directory.Exists(dir)) return;
                int deleted = 0;
                string[] files = System.IO.Directory.GetFiles(dir, "red-alliance-*.cfg");
                for (int i = 0; i < files.Length; i++)
                {
                    try { System.IO.File.Delete(files[i]); deleted++; }
                    catch (Exception e) { Logger.LogWarning($"[savewipe] couldn't delete '{files[i]}': {e.Message}"); }
                }
                if (deleted > 0)
                    Logger.LogInfo($"[savewipe] deleted {deleted} save slot(s) on restart (DeleteSavesOnRestart=true).");
            }
            catch (Exception e)
            {
                Logger.LogError("[savewipe] failed: " + e);
            }
        }

        private void ToggleFpsCap()
        {
            if (!_fpsLockEnabled.Value) { Logger.LogMessage("[fps] FPSLockEnabled=false, toggle ignored"); return; }
            _fpsLow = !_fpsLow;
            ApplyFpsCap();
            Logger.LogMessage($"[fps] cap = {(_fpsLow ? _minFPS.Value : _standartFPS.Value)} ({(_fpsLow ? "LOW" : "STANDART")})");
        }

        private void ApplyFpsCap()
        {
            if (_fpsLockEnabled == null || !_fpsLockEnabled.Value) return;
            int target = _fpsLow ? _minFPS.Value : _standartFPS.Value;
            // targetFrameRate is ignored while vSyncCount > 0, so force vsync off.
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = target;
        }

        private void EnsureGuiStyle()
        {
            if (_timerStyle != null) return;
            _timerStyle = new GUIStyle(GUI.skin.label);
            _timerStyle.fontSize = _timerFontSize.Value;
            _timerStyle.fontStyle = FontStyle.Bold;
            _timerStyle.normal.textColor = Color.white;
            _timerStyle.alignment = TextAnchor.UpperLeft;

            _timerShadowStyle = new GUIStyle(_timerStyle);
            _timerShadowStyle.normal.textColor = Color.black;
        }

        private float _lastMenuErrorTime;

        private void OnGUI()
        {
            // Timers first: a menu-drawing exception must never take the timers down with it.
            DrawTimers();

            if (RaMenu.Visible)
            {
                try
                {
                    RaMenu.Draw(0x5E5E0042);
                }
                catch (Exception e)
                {
                    if (Time.unscaledTime - _lastMenuErrorTime > 5f)
                    {
                        _lastMenuErrorTime = Time.unscaledTime;
                        Logger.LogError("[menu] draw failed: " + e);
                    }
                }
            }
        }

        private void DrawTimers()
        {
            if (_showTimers == null || !_showTimers.Value) return;
            EnsureGuiStyle();

            int x = _timerScreenX.Value;
            int y = _timerScreenY.Value;
            int rowH = _timerFontSize.Value + 4;

            string rtaText = $"RTA  {_rta.Format()}";
            string igtText = $"IGT  {_igt.Format()}";

            // Drop shadow for readability over varied backgrounds.
            GUI.Label(new Rect(x + 1, y + 1, 400, rowH), rtaText, _timerShadowStyle);
            GUI.Label(new Rect(x, y, 400, rowH), rtaText, _timerStyle);

            GUI.Label(new Rect(x + 1, y + rowH + 1, 400, rowH), igtText, _timerShadowStyle);
            GUI.Label(new Rect(x, y + rowH, 400, rowH), igtText, _timerStyle);
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
