using HarmonyLib;
using UnityEngine;

namespace RedAllianceSpeedrun
{
    // Fixes the black-screen-on-restart bug (ported from the v1.3 plugin).
    //
    // LoadLevelCoroutine resets the player / canvas manager and draws the loading screen,
    // waits two frames, and only THEN starts the private LoadLevel coroutine — whose first
    // line is 'if (LoadingLevel) yield break'. If a second load starts inside that window
    // (TAB racing a level-end trigger, double scripted load), the loser passes the
    // destruction phase and then aborts in the private guard: permanent black screen.
    //
    // v1.4 difference: the public LoadLevel legitimately REPLACES a load that is still
    // buffered in its additionalDelay wait (waitingForLoading == true, it force-stops the
    // old coroutine). The guard must allow that, and only reject calls arriving once the
    // committed load is past the buffer (destruction underway or imminent).
    [HarmonyPatch(typeof(SceneManagerScript), nameof(SceneManagerScript.LoadLevel),
        new System.Type[] { typeof(string), typeof(float), typeof(bool), typeof(Vector3), typeof(Vector3), typeof(LevelLoadingType) })]
    internal static class LoadLevelDuplicateGuardPatch
    {
        internal static bool Pending;
        internal static float PendingSince;
        private const float PendingTimeout = 30f;

        private static readonly AccessTools.FieldRef<SceneManagerScript, bool> WaitingForLoadingRef =
            AccessTools.FieldRefAccess<SceneManagerScript, bool>("waitingForLoading");

        [HarmonyPrefix]
        private static bool Prefix(SceneManagerScript __instance, string nextLevelString)
        {
            bool pendingActive = Pending && (Time.unscaledTime - PendingSince) < PendingTimeout;
            bool buffered = false;
            try { buffered = WaitingForLoadingRef(__instance); } catch { }

            // Replacing a still-buffered (delayed) load is original, supported behaviour.
            if (SceneManagerScript.LoadingLevel || (pendingActive && !buffered))
            {
                Plugin.Logger.LogWarning(
                    $"[loadguard] LoadLevel('{nextLevelString}') rejected: another load is in progress " +
                    $"(LoadingLevel={SceneManagerScript.LoadingLevel}, pending={pendingActive}). " +
                    "Without this guard the duplicate would reset the player and hang on a black screen.");
                return false; // skip original — nothing gets destroyed
            }
            Pending = true;
            PendingSince = Time.unscaledTime;
            return true;
        }

        internal static void Clear()
        {
            Pending = false;
        }
    }

    // Two jobs on GameSaveScript.LoadGameRemote (ported from the v1.3 plugin):
    //
    // 1) Empty-slot guard. The original resets the player and canvas manager immediately,
    //    then the Load coroutine opens the slot file. A missing/empty slot (inevitable after
    //    DeleteSavesOnRestart wipes them) fails after the destruction — black screen.
    //    Reject the load before anything is touched.
    //
    // 2) Marks the next scene load as "caused by loading a save" so the autosplitter
    //    suppresses the split: jumping levels via a save is not run progress.
    [HarmonyPatch(typeof(GameSaveScript), nameof(GameSaveScript.LoadGameRemote))]
    internal static class SaveLoadDetectPatch
    {
        internal static bool Pending;
        internal static float PendingSince;
        private const float PendingTimeout = 60f;

        [HarmonyPrefix]
        private static bool Prefix(Config cfg)
        {
            bool valid = false;
            try
            {
                valid = cfg != null
                    && System.IO.File.Exists(cfg.path)
                    && new System.IO.FileInfo(cfg.path).Length > 0;
            }
            catch { }
            if (!valid)
            {
                Plugin.Logger.LogWarning(
                    "[saveguard] LoadGameRemote rejected: save slot file is missing or empty. " +
                    "The original would reset the player first and hang on a black screen.");
                try { DeveloperConsoleScript.AddConsoleMessage("Save slot is empty — load cancelled."); }
                catch { }
                return false; // skip original — nothing gets destroyed
            }

            Pending = true;
            PendingSince = Time.unscaledTime;
            Plugin.Logger.LogInfo("[splitguard] save load started — next scene change will not split.");
            return true;
        }

        internal static bool IsActive()
        {
            return Pending && (Time.unscaledTime - PendingSince) < PendingTimeout;
        }

        internal static void Clear()
        {
            Pending = false;
        }
    }
}
