using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace RedAllianceSpeedrun
{
    [BepInPlugin(GUID, "Red Alliance Speedrun Mod", "2.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        public const string GUID = "redalliance.speedrun.v2";

        internal new static ManualLogSource Logger;
        internal static ConfigFile ConfigRef;
        internal static Plugin Instance;

        // Hotkeys
        private ConfigEntry<KeyCode> _restartKey;
        private ConfigEntry<KeyCode> _diagKey;
        private ConfigEntry<KeyCode> _toggleClipKey;
        private ConfigEntry<KeyCode> _fpsToggleKey;
        private ConfigEntry<KeyCode> _forceSaveKey;
        private ConfigEntry<KeyCode> _menuToggleKey;

        // FPS lock
        private ConfigEntry<int> _standartFPS;
        private ConfigEntry<int> _minFPS;
        private bool _fpsLow;

        // Clip-through behavior
        private ConfigEntry<bool> _autoEnableAllowClip;
        private ConfigEntry<bool> _bypassCrouchCeilingCheck;
        private ConfigEntry<bool> _instantUncrouch;
        private ConfigEntry<float> _instantUncrouchMinDeltaMs;
        private ConfigEntry<bool> _postClipPreserveSpeed;
        private ConfigEntry<int> _postClipBhopTicks;
        private ConfigEntry<bool> _postClipPreserveForward;
        private ConfigEntry<bool> _clipPenetrationPush;
        private ConfigEntry<float> _clipPenetrationPushMinSpeed;
        private ConfigEntry<float> _clipPenetrationPushMargin;
        private ConfigEntry<float> _clipPenetrationMaxPush;
        private ConfigEntry<int> _clipPenetrationLayerMask;
        private ConfigEntry<float> _clipPenetrationFloorDot;
        private ConfigEntry<int> _clipPenetrationIterations;
        private ConfigEntry<float> _clipPenetrationStepCap;
        private ConfigEntry<float> _clipPenetrationDepthDeadzone;
        private ConfigEntry<bool> _logClipEvents;

        // Restart
        private ConfigEntry<string> _restartLevel;
        private ConfigEntry<bool> _restartInMenu;
        private ConfigEntry<bool> _restartSetDifficulty;
        private ConfigEntry<int> _restartDifficultyValue;
        private ConfigEntry<bool> _speedrunMode;
        private ConfigEntry<bool> _practiceMode;
        private ConfigEntry<bool> _livesplitEnabled;
        private ConfigEntry<string> _livesplitHost;
        private ConfigEntry<int> _livesplitPort;
        private ConfigEntry<float> _livesplitSyncRate;
        private ConfigEntry<string> _livesplitSplitScenes;
        private ConfigEntry<bool> _livesplitSplitOnLevelChange;

        // Timers
        private ConfigEntry<bool> _showTimers;
        private ConfigEntry<bool> _skipPrison1;
        private ConfigEntry<int> _timerScreenX;
        private ConfigEntry<int> _timerScreenY;
        private ConfigEntry<int> _timerFontSize;
        private ConfigEntry<string> _runStartScene;
        private ConfigEntry<string> _runEndScene;

        internal static bool BypassCrouchCeiling;
        internal static bool InstantUncrouch;
        internal static float InstantUncrouchMinDeltaMs;
        internal static bool PostClipPreserveSpeed;
        internal static int PostClipBhopTicks;
        internal static bool PostClipPreserveForward;
        internal static bool ClipPenetrationPush;
        internal static float ClipPenetrationPushMinSpeed;
        internal static float ClipPenetrationPushMargin;
        internal static float ClipPenetrationMaxPush;
        internal static int ClipPenetrationLayerMask;
        internal static float ClipPenetrationFloorDot;
        internal static int ClipPenetrationIterations;
        internal static float ClipPenetrationStepCap;
        internal static float ClipPenetrationDepthDeadzone;
        internal static bool LogClipEvents;
        internal static bool SpeedrunMode;
        internal static bool PracticeMode;
        internal static bool LiveSplitEnabled;
        internal static string LiveSplitHost;
        internal static int LiveSplitPort;
        internal static float LiveSplitSyncRate;
        private float _livesplitNextSyncTime;
        private string[] _livesplitSplitSceneList;
        private string _livesplitLastSplitScene;
        private int _lastSplitBuildIndex = -1; // splits only fire for scenes with a HIGHER build index
        private ConfigEntry<bool> _deleteSavesOnRestart;
        private string _pendingStartScene; // target scene name; timer starts when SceneManager loads this one

        // Timer state
        private readonly SpeedrunTimer _rta = new SpeedrunTimer("RTA", false, false);
        private readonly SpeedrunTimer _igt = new SpeedrunTimer("IGT", true, true);
        private bool _waitingForLoadComplete; // after TAB, start timers when load completes
        private bool _runIsActive; // true between first start and credits stop

        // GUI cache
        private GUIStyle _timerStyle;
        private GUIStyle _timerShadowStyle;

        private float _lastRestartTime;
        private const float RestartDebounce = 0.5f;

        private void Awake()
        {
            Logger = base.Logger;
            ConfigRef = Config;
            Instance = this;

            // --- Hotkeys ---
            _restartKey = Config.Bind(
                "Hotkeys", "RestartKey", KeyCode.Tab,
                "Restart the speedrun: reload RestartLevel scene (or current scene if blank), reset both timers.");
            _diagKey = Config.Bind(
                "Hotkeys", "DiagnosticsKey", KeyCode.F11,
                "Prints memory + state diagnostics to BepInEx/LogOutput.log.");
            _toggleClipKey = Config.Bind(
                "Hotkeys", "ToggleAllowClipKey", KeyCode.F8,
                "Toggle FPSWalker.AllowClip at runtime.");
            _fpsToggleKey = Config.Bind(
                "Hotkeys", "ToggleFPSKey", KeyCode.Q,
                "Hotkey to swap Application.targetFrameRate between StandartFPS and MinFPS. " +
                "Low FPS (long Time.deltaTime) is used for certain frame-timing tricks.");
            _forceSaveKey = Config.Bind(
                "Hotkeys", "ForceSaveKey", KeyCode.F5,
                "Save-anywhere key for SpeedrunMode. Calls GameSaveScript.SaveRemote " +
                "directly, bypassing the VortexRun input gate. Only active when SpeedrunMode " +
                "is true.");
            _menuToggleKey = Config.Bind(
                "Hotkeys", "ToggleMenuKey", KeyCode.F10,
                "Toggle the in-game speedrun menu (mode selector, level launcher, config editor).");

            // --- FPS lock ---
            _standartFPS = Config.Bind(
                "FPS", "StandartFPS", 100,
                "Target FPS for normal gameplay. Set to -1 to uncap.");
            _minFPS = Config.Bind(
                "FPS", "MinFPS", 5,
                "Target FPS when toggled low (Q by default). Intentionally low to produce big " +
                "frame deltas — useful for tricks that need a lag-spike-like timing.");

            // --- Restart ---
            _restartLevel = Config.Bind(
                "Restart", "RestartLevel", "",
                "Scene name to load when RestartKey is pressed. Leave EMPTY to reload the " +
                "currently active scene. Example: 'mountains_1', 'prison_1', 'prologue_1'.");
            _restartInMenu = Config.Bind(
                "Restart", "RestartInMenu", false,
                "If false, the restart key is ignored when the active scene is main_menu, " +
                "credits, or start_screen.");
            _restartSetDifficulty = Config.Bind(
                "Restart", "RestartSetDifficulty", true,
                "On TAB restart, force NetworkManager.gameDifficulty to RestartDifficultyValue. " +
                "Game auto-resets to Normal (1) on relog; this overrides back to your run value.");
            _restartDifficultyValue = Config.Bind(
                "Restart", "RestartDifficultyValue", 0,
                "Difficulty level set on restart. 0=Easy, 1=Normal, 2=Hard, 3=Hardcore, 4=UltraHardcore.");
            _speedrunMode = Config.Bind(
                "Restart", "SpeedrunMode", false,
                "Speedrunmod%. Forces VortexRun=true (fast level paths), VortexLegacyMode=" +
                "false (saves work), gameDifficulty=Easy. On TAB restart starts at prologue_1 " +
                "with timers at 0 (ignores SkipPrison1). LoadLevel('prison_1') → redirects " +
                "to prison_2 (skip 38s cutscene). Save UI button + F5 unblocked. Re-applied " +
                "on every SceneLoaded so disableVortexMode triggers don't undo it.");
            SpeedrunMode = _speedrunMode.Value;
            _deleteSavesOnRestart = Config.Bind(
                "Restart", "DeleteSavesOnRestart", true,
                "Delete all save slots (Assets/SaveData/red-alliance-*.cfg, including the " +
                "vortex slot) on every TAB restart / menu level launch. Prevents loading a " +
                "pre-existing save to jump levels ahead mid-run. Saves made DURING the run " +
                "are unaffected until the next restart. gameData.cfg (settings/achievements) " +
                "is never touched. Set false if you alternate runs with a casual playthrough.");
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

            // --- Clip-through ---
            _autoEnableAllowClip = Config.Bind(
                "ClipThrough", "AutoEnableAllowClip", true,
                "Set FPSWalker.AllowClip = true on every scene load. Re-enables the " +
                "wall-clip flag the new build gates behind it.");
            _bypassCrouchCeilingCheck = Config.Bind(
                "ClipThrough", "BypassCrouchCeilingCheck", false,
                "Zero curHitCrouchColliders after each CrouchController call. Allows " +
                "uncrouch in tight ceilings.");
            BypassCrouchCeiling = _bypassCrouchCeilingCheck.Value;
            _instantUncrouch = Config.Bind(
                "ClipThrough", "InstantUncrouch", true,
                "On SetCrouchingState(false) during a lag spike, snap capsule height/center " +
                "to default same-frame — mimics OLD coroutine behaviour.");
            InstantUncrouch = _instantUncrouch.Value;
            _instantUncrouchMinDeltaMs = Config.Bind(
                "ClipThrough", "InstantUncrouchMinDeltaMs", 50f,
                "Min Time.unscaledDeltaTime (ms) for the snap to fire. OLD formula " +
                "clamped Lerp at dt>=40ms.");
            InstantUncrouchMinDeltaMs = _instantUncrouchMinDeltaMs.Value;
            _postClipPreserveSpeed = Config.Bind(
                "ClipThrough", "PostClipPreserveSpeed", false,
                "Hold curBhopBuffer=0 for N ticks after clip-snap so MoveGround takes the " +
                "bhop branch (no friction) during Unity's FixedUpdate catch-up. Off by " +
                "default — native physics handles velocity naturally in most cases.");
            PostClipPreserveSpeed = _postClipPreserveSpeed.Value;
            _postClipBhopTicks = Config.Bind(
                "ClipThrough", "PostClipBhopTicks", 15,
                "How many FixedUpdate ticks the bhop window stays open. 15 covers typical " +
                "lag-spike catch-up; 30 covers worst case.");
            PostClipBhopTicks = _postClipBhopTicks.Value;
            _postClipPreserveForward = Config.Bind(
                "ClipThrough", "PostClipPreserveForward", false,
                "Quake-style positive-num2 clamp in Accelerate during the bhop window — " +
                "prevents the cap from actively decelerating speed past the limit. Off by " +
                "default. Requires PostClipPreserveSpeed=true to have effect.");
            PostClipPreserveForward = _postClipPreserveForward.Value;
            _clipPenetrationPush = Config.Bind(
                "ClipThrough", "ClipPenetrationPush", true,
                "Replicates Unity 2017 CC penetration response. After snap, iterates " +
                "OverlapCapsule + ComputePenetration → push along velocity by depth until " +
                "clear. Selective by geometry: velocity into wall = clip, parallel = no-op.");
            ClipPenetrationPush = _clipPenetrationPush.Value;
            _clipPenetrationPushMinSpeed = Config.Bind(
                "ClipThrough", "ClipPenetrationPushMinSpeed", 0f,
                "Min horizontal velocity (m/s) to arm the push. 0 = no speed gate.");
            ClipPenetrationPushMinSpeed = _clipPenetrationPushMinSpeed.Value;
            _clipPenetrationPushMargin = Config.Bind(
                "ClipThrough", "ClipPenetrationPushMargin", 0.05f,
                "Extra distance (m) added to depth each iter for float-precision exit. " +
                "Higher = cleaner exit, slight overshoot.");
            ClipPenetrationPushMargin = _clipPenetrationPushMargin.Value;
            _clipPenetrationMaxPush = Config.Bind(
                "ClipThrough", "ClipPenetrationMaxPush", 2.0f,
                "Safety cap on TOTAL push distance (m) across all iters.");
            ClipPenetrationMaxPush = _clipPenetrationMaxPush.Value;
            _clipPenetrationLayerMask = Config.Bind(
                "ClipThrough", "ClipPenetrationLayerMask", -1,
                "LayerMask for overlap query. -1 = all layers.");
            ClipPenetrationLayerMask = _clipPenetrationLayerMask.Value;
            _clipPenetrationFloorDot = Config.Bind(
                "ClipThrough", "ClipPenetrationFloorDot", 0.5f,
                "Skip colliders whose ComputePenetration direction.y exceeds this " +
                "(floors/steps below player). Prevents horizontal scoot off edges. " +
                "0.5 ≈ 60° cone.");
            ClipPenetrationFloorDot = _clipPenetrationFloorDot.Value;
            _clipPenetrationIterations = Config.Bind(
                "ClipThrough", "ClipPenetrationIterations", 8,
                "Max iterations of push + re-query. Stops earlier on clear / deadzone / " +
                "no progress.");
            ClipPenetrationIterations = _clipPenetrationIterations.Value;
            _clipPenetrationStepCap = Config.Bind(
                "ClipThrough", "ClipPenetrationStepCap", 0.4f,
                "Max push (m) per iteration. Prevents teleport-through-thin-wall on big " +
                "depth readings — big walls clear over multiple smaller iters.");
            ClipPenetrationStepCap = _clipPenetrationStepCap.Value;
            _clipPenetrationDepthDeadzone = Config.Bind(
                "ClipThrough", "ClipPenetrationDepthDeadzone", 0.15f,
                "Stop iterating when depth < this. Player ends ON wall surface, native " +
                "CC.Move resolves the rest. Critical for jump-after-clip — prevents " +
                "overshoot into void where isGrounded = false.");
            ClipPenetrationDepthDeadzone = _clipPenetrationDepthDeadzone.Value;
            _logClipEvents = Config.Bind(
                "ClipThrough", "LogClipEvents", false,
                "Per-event log lines for snap/push/bhop. Useful for tuning, noisy in normal play.");
            LogClipEvents = _logClipEvents.Value;

            // --- Timers ---
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
            _runStartScene = Config.Bind(
                "Timers", "RunStartSceneHint", "",
                "Optional informational note about which scene starts the run. Not used by " +
                "the code — actual start scene is RestartLevel under [Restart].");
            _runEndScene = Config.Bind(
                "Timers", "RunEndScene", "credits",
                "Scene name that ends the run. Both timers stop when this scene loads.");

            SceneManager.sceneLoaded += OnSceneLoaded;

            try
            {
                var harmony = new Harmony(GUID);
                harmony.PatchAll(typeof(CrouchControllerBypassPatch));
                harmony.PatchAll(typeof(InstantUncrouchPatch));
                harmony.PatchAll(typeof(PostClipBhopPatch));
                harmony.PatchAll(typeof(PostClipAcceleratePatch));
                harmony.PatchAll(typeof(SpeedrunSkipPrison1Patch));
                harmony.PatchAll(typeof(SpeedrunSkipPrison1RpcPatch));
                harmony.PatchAll(typeof(SpeedrunSkipPrison1AsyncPatch));
                harmony.PatchAll(typeof(SpeedrunSaveButtonEnablePatch));
                harmony.PatchAll(typeof(SpeedrunDamageMultiplierPatch));
                harmony.PatchAll(typeof(SpeedrunBlockAutosaveRemotePatch));
                harmony.PatchAll(typeof(SpeedrunBlockAutosaveScriptPatch));
                harmony.PatchAll(typeof(ConsoleConfigCommandPatch));
                harmony.PatchAll(typeof(LoadLevelDuplicateGuardPatch));
                harmony.PatchAll(typeof(SaveLoadDetectPatch));
                Logger.LogInfo("[clip] Harmony patches installed (CrouchController, SetCrouchingState, CasualController, Accelerate, LoadLevel, PauseMenu.Pause, load guards).");
            }
            catch (Exception e)
            {
                Logger.LogError("[clip] Failed to install Harmony patches: " + e);
            }

            Logger.LogInfo($"Restart: key={_restartKey.Value} level='{_restartLevel.Value}'; SkipPrison1={_skipPrison1.Value}");
            Logger.LogInfo($"FPS: StandartFPS={_standartFPS.Value} MinFPS={_minFPS.Value} toggle={_fpsToggleKey.Value}");
            ApplyAllowClipIfEnabled();
            ApplyFpsCap();
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            LiveSplitClient.Disconnect();
        }

        // Copies ConfigEntry.Value into the internal static fields. Called by Awake (after
        // all Config.Bind calls) and by the racfg console command after a runtime edit so
        // patches that read the statics see new values without a restart.
        internal void SyncStatics()
        {
            BypassCrouchCeiling = _bypassCrouchCeilingCheck.Value;
            InstantUncrouch = _instantUncrouch.Value;
            InstantUncrouchMinDeltaMs = _instantUncrouchMinDeltaMs.Value;
            PostClipPreserveSpeed = _postClipPreserveSpeed.Value;
            PostClipBhopTicks = _postClipBhopTicks.Value;
            PostClipPreserveForward = _postClipPreserveForward.Value;
            ClipPenetrationPush = _clipPenetrationPush.Value;
            ClipPenetrationPushMinSpeed = _clipPenetrationPushMinSpeed.Value;
            ClipPenetrationPushMargin = _clipPenetrationPushMargin.Value;
            ClipPenetrationMaxPush = _clipPenetrationMaxPush.Value;
            ClipPenetrationLayerMask = _clipPenetrationLayerMask.Value;
            ClipPenetrationFloorDot = _clipPenetrationFloorDot.Value;
            ClipPenetrationIterations = _clipPenetrationIterations.Value;
            ClipPenetrationStepCap = _clipPenetrationStepCap.Value;
            ClipPenetrationDepthDeadzone = _clipPenetrationDepthDeadzone.Value;
            LogClipEvents = _logClipEvents.Value;
            SpeedrunMode = _speedrunMode.Value;
            PracticeMode = _practiceMode.Value;
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

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (mode != LoadSceneMode.Single) return;

            // A load finished — re-arm the duplicate-load guard for the next one.
            LoadLevelDuplicateGuardPatch.Clear();

            ApplyAllowClipIfEnabled();
            ApplySpeedrunModeIfEnabled();
            ApplyCheatGate();

            // Timer start: only when our designated start scene actually finishes loading
            // (after click-to-continue, after asyncLoadingOperation.allowSceneActivation =
            // true). Triggered exactly once per restart. _runIsActive becomes true here,
            // so the auto-split block below sees it during scene transitions, but we mark
            // _livesplitLastSplitScene = scene.name to prevent splitting the start scene.
            if (_waitingForLoadComplete && !string.IsNullOrEmpty(_pendingStartScene)
                && scene.name == _pendingStartScene)
            {
                double initial = (!_speedrunMode.Value && _skipPrison1.Value) ? 38.470 : 0.0;
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

            // End-of-run check: stop both timers when credits load.
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
                var lines = new System.Collections.Generic.List<string>();
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
                    $"plugin_version=2.0.0",
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

        private void ApplySpeedrunModeIfEnabled()
        {
            if (_speedrunMode == null || !_speedrunMode.Value) return;
            try
            {
                ExtrasSettingsScript.VortexRun = true;
                ExtrasSettingsScript.VortexLegacyMode = false;
                NetworkManager.gameDifficulty = GameDifficulty.Easy;
                var pool = PlayerPool.Instance;
                if ((object)pool != null && (object)pool.GameSettingsScript != null)
                    pool.GameSettingsScript.UpdateDifficultyLabel();
                Logger.LogInfo("[speedrun] VortexRun=true, VortexLegacyMode=false, gameDifficulty=Easy");
            }
            catch (Exception e)
            {
                Logger.LogError("[speedrun] Failed to apply: " + e);
            }
        }

        private void ApplyAllowClipIfEnabled()
        {
            if (_autoEnableAllowClip == null || !_autoEnableAllowClip.Value) return;
            try
            {
                if (!FPSWalker.AllowClip)
                {
                    FPSWalker.AllowClip = true;
                    Logger.LogInfo("[clip] FPSWalker.AllowClip = true");
                }
            }
            catch (Exception e)
            {
                Logger.LogError("[clip] Failed to set AllowClip: " + e);
            }
        }

        private void Update()
        {
            // Timer logic: if a TAB-triggered scene load is in progress, start timers once
            // the loading screen disappears (LoadingLevel returns to false).
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
            double dt = Time.unscaledDeltaTime;
            bool isLoading = SceneManagerScript.LoadingLevel;
            bool isPaused = false;
            try { isPaused = PauseMenuScript.Paused; } catch { }
            _rta.Tick(dt, isLoading, isPaused);
            _igt.Tick(dt, isLoading, isPaused);

            // Input
            if (Input.GetKeyDown(_restartKey.Value)) TryQuickRestart();
            if (Input.GetKeyDown(_diagKey.Value)) LogDiagnostics("manual");
            if (Input.GetKeyDown(_toggleClipKey.Value)) ToggleAllowClip();
            if (Input.GetKeyDown(_fpsToggleKey.Value)) ToggleFpsCap();
            if (_speedrunMode.Value && Input.GetKeyDown(_forceSaveKey.Value)) TryForceSave();
            if (Input.GetKeyDown(_menuToggleKey.Value)) RaMenu.Toggle();

            // Enforce target FPS every frame — game settings menus, vsync changes, etc. may
            // overwrite it otherwise. Cheap to set.
            ApplyFpsCap();
        }

        private void ToggleFpsCap()
        {
            _fpsLow = !_fpsLow;
            ApplyFpsCap();
            Logger.LogMessage($"[fps] cap = {(_fpsLow ? _minFPS.Value : _standartFPS.Value)} ({(_fpsLow ? "LOW" : "STANDART")})");
        }

        private void ApplyFpsCap()
        {
            int target = _fpsLow ? _minFPS.Value : _standartFPS.Value;
            // targetFrameRate is ignored while vSyncCount > 0, so force vsync off.
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = target;
        }

        private void TryQuickRestart()
        {
            if (Time.unscaledTime - _lastRestartTime < RestartDebounce) return;
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

            // Target scene: SpeedrunMode forces prologue_1; else configured RestartLevel
            // if non-empty, else current scene.
            string target;
            if (_speedrunMode.Value)
            {
                target = "prologue_1";
            }
            else
            {
                target = (_restartLevel.Value != null && _restartLevel.Value.Length > 0)
                    ? _restartLevel.Value
                    : active;
            }

            var loadType = (target == "main_menu" || target == "credits")
                ? LevelLoadingType.Reset
                : LevelLoadingType.Transfer;

            Logger.LogInfo($"Quick restart -> '{target}' (from '{active}', loadType={loadType}).");
            try { NetworkManager.OnStartedNewGame(); }
            catch (Exception e) { Logger.LogWarning("OnStartedNewGame threw: " + e.Message); }

            if (_restartSetDifficulty.Value && !_speedrunMode.Value)
            {
                try
                {
                    int diffInt = Mathf.Clamp(_restartDifficultyValue.Value, 0, 4);
                    NetworkManager.gameDifficulty = (GameDifficulty)diffInt;
                    var pool = PlayerPool.Instance;
                    if ((object)pool != null && (object)pool.GameSettingsScript != null)
                        pool.GameSettingsScript.UpdateDifficultyLabel();
                    Logger.LogInfo($"[restart] gameDifficulty = {(GameDifficulty)diffInt} ({diffInt})");
                }
                catch (Exception e) { Logger.LogWarning("Set difficulty failed: " + e.Message); }
            }

            // Speedrun mode override — applied also in OnSceneLoaded but eagerly here so
            // the loading screen starts with correct flags.
            ApplySpeedrunModeIfEnabled();
            ApplyCheatGate();

            // Reset and prime the timers — they'll start in Update once LoadingLevel becomes false.
            // SpeedrunMode forces initial=0 regardless of SkipPrison1.
            double initial = (!_speedrunMode.Value && _skipPrison1.Value) ? 38.470 : 0.0;
            _rta.Reset(initial);
            _igt.Reset(initial);
            _waitingForLoadComplete = true;
            _runIsActive = false;
            _livesplitLastSplitScene = null;
            _lastSplitBuildIndex = -1;
            _pendingStartScene = target;
            LiveSplitClient.Reset();
            if (_deleteSavesOnRestart != null && _deleteSavesOnRestart.Value)
            {
                DeleteSaveSlots();
            }

            smgr.LoadLevel(target, 0f, false, Vector3.zero, Vector3.zero, loadType);
        }

        // Called by RaMenu before launching a level so timers behave the same as a TAB restart.
        internal void PrepareTimersForLaunch(string sceneName)
        {
            double initial = (!_speedrunMode.Value && _skipPrison1.Value) ? 38.470 : 0.0;
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

        // Deletes the save-slot files (including the vortex slot) so a pre-existing save
        // can't be loaded mid-run to jump levels. Only red-alliance-*.cfg slots —
        // gameData.cfg (settings, achievements, counters) is never touched.
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

        private void TryForceSave()
        {
            try
            {
                var save = GameSaveScript.Instance;
                if ((object)save == null) { Logger.LogWarning("[speedrun] save: GameSaveScript.Instance null"); return; }
                save.SaveRemote(autoSave: false);
                Logger.LogInfo("[speedrun] force save invoked");
            }
            catch (Exception e)
            {
                Logger.LogError("[speedrun] force save failed: " + e);
            }
        }

        private void ToggleAllowClip()
        {
            try
            {
                FPSWalker.AllowClip = !FPSWalker.AllowClip;
                Logger.LogMessage($"[clip] AllowClip = {FPSWalker.AllowClip}");
            }
            catch (Exception e)
            {
                Logger.LogError("[clip] Toggle failed: " + e);
            }
        }

        internal void LogDiagnostics(string tag)
        {
            try
            {
                long monoMb = GC.GetTotalMemory(false) / (1024L * 1024L);
                bool allowClip = false;
                try { allowClip = FPSWalker.AllowClip; } catch { }
                bool paused = false;
                try { paused = PauseMenuScript.Paused; } catch { }
                Logger.LogMessage(
                    $"[diag {tag}] mono={monoMb}MB  AllowClip={allowClip}  scene={SceneManager.GetActiveScene().name}  loading={SceneManagerScript.LoadingLevel}  paused={paused}  runActive={_runIsActive}  RTA={_rta.Format()}  IGT={_igt.Format()}");
            }
            catch (Exception e)
            {
                Logger.LogError("Diagnostics failed: " + e);
            }
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

        private void OnGUI()
        {
            if (RaMenu.Visible) RaMenu.Draw(0x5E5E0042);
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
    }
}
